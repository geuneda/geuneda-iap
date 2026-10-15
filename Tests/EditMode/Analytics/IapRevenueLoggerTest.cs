using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Geuneda.Analytics;
using Geuneda.Core;
using Geuneda.Iap.Analytics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Tests
{
	public sealed class IapRevenueLoggerTest : IapServiceTestBase
	{
		private RecordingAnalytics _analytics;

		[SetUp]
		public void SetUpAnalytics()
		{
			_analytics = new RecordingAnalytics();
		}

		[Test]
		public void ConfirmedPurchase_IsLoggedOnceAsThePurchaseStandardEvent()
		{
			using (new IapRevenueLogger(Service, _analytics))
			{
				Initialize();
				CompletePurchasesImmediately();

				var result = Purchase(Gem);

				Assert.That(result.Value.IsSuccess, Is.True);
				Assert.That(_analytics.Events, Has.Count.EqualTo(1));
				var logged = _analytics.Events[0];
				Assert.That(logged.Name, Is.EqualTo(AnalyticsStandardEvents.Purchase));
				Assert.That(logged.Sinks, Is.EqualTo(_analytics.DefaultSinks));
				Assert.That(logged.Parameters[AnalyticsStandardParameters.ProductId], Is.EqualTo(Gem.Id));
				Assert.That(logged.Parameters[AnalyticsStandardParameters.Price], Is.EqualTo(1100d));
				Assert.That(logged.Parameters[AnalyticsStandardParameters.Currency], Is.EqualTo("KRW"));
				Assert.That(logged.Parameters[AnalyticsStandardParameters.Quantity], Is.EqualTo(1));
				Assert.That(logged.Parameters[AnalyticsStandardParameters.TransactionId], Is.EqualTo(result.Value.Value.Order.TransactionId));
				Assert.That(logged.Parameters[AnalyticsStandardParameters.ProductName], Is.EqualTo("gem_100 title"));
			}
		}

		[Test]
		public void ServerResponse_UsesTheVerifiedAmount_AndSkipsTestAndRevokedPurchases()
		{
			using (new IapRevenueLogger(Service, _analytics))
			{
				Initialize();
				CompletePurchasesImmediately();
				Fulfiller.Next(FulfillmentResult.Granted(new IapRedeemResponse<object> { Status = IapRedeemStatus.Granted, PaidAmount = 990m, PaidCurrency = "JPY" }));
				Fulfiller.Next(FulfillmentResult.Granted(new IapRedeemResponse<object> { Status = IapRedeemStatus.Granted, IsTest = true, PaidAmount = 1m, PaidCurrency = "USD" }));
				Fulfiller.Next(FulfillmentResult.AlreadyGranted(new IapRedeemResponse<object> { Status = IapRedeemStatus.AlreadyGranted, Code = IapRedeemCodes.Revoked }));
				Fulfiller.Next(FulfillmentResult.Granted(new IapRedeemResponse<object> { Status = IapRedeemStatus.Granted }));

				Purchase(Gem);
				Purchase(Gem);
				Purchase(Gem);
				Purchase(Gem);

				Assert.That(_analytics.Events, Has.Count.EqualTo(2), "test and revoked purchases are not revenue");
				Assert.That(_analytics.Events[0].Parameters[AnalyticsStandardParameters.Price], Is.EqualTo(990d));
				Assert.That(_analytics.Events[0].Parameters[AnalyticsStandardParameters.Currency], Is.EqualTo("JPY"));
				Assert.That(_analytics.Events[1].Parameters[AnalyticsStandardParameters.Price], Is.EqualTo(1100d), "no server amount falls back to the store price");
			}
		}

		[Test]
		public void ServerQuantity_SplitsTheTotalOrMultipliesTheStorePrice()
		{
			// 서버 금액은 수량을 포함한 총액(App Store price, Google 주문 total)이다. 이벤트는 단가 × 수량으로 기록한다
			using (new IapRevenueLogger(Service, _analytics))
			{
				Initialize();
				CompletePurchasesImmediately();
				Fulfiller.Next(FulfillmentResult.Granted(new IapRedeemResponse<object>
					{ Status = IapRedeemStatus.Granted, Quantity = 3, PaidAmount = 3000m, PaidCurrency = "KRW" }));
				Fulfiller.Next(FulfillmentResult.Granted(new IapRedeemResponse<object> { Status = IapRedeemStatus.Granted, Quantity = 3 }));

				Purchase(Gem);
				Purchase(Gem);

				Assert.That(_analytics.Events, Has.Count.EqualTo(2));
				Assert.That(_analytics.Events[0].Parameters[AnalyticsStandardParameters.Price], Is.EqualTo(1000d));
				Assert.That(_analytics.Events[0].Parameters[AnalyticsStandardParameters.Quantity], Is.EqualTo(3));
				Assert.That(_analytics.Events[1].Parameters[AnalyticsStandardParameters.Price], Is.EqualTo(1100d));
				Assert.That(_analytics.Events[1].Parameters[AnalyticsStandardParameters.Quantity], Is.EqualTo(3));
			}
		}

		[Test]
		public void FailedConfirmation_IsLoggedOnlyWhenTheRetrySucceeds()
		{
			using (new IapRevenueLogger(Service, _analytics))
			{
				Initialize();
				CompletePurchasesImmediately();
				var attempts = 0;
				Store.ConfirmHandler = order => UniTask.FromResult(++attempts == 1
					? IapConfirmResult.Failed(IapStoreFailureReason.StoreUnavailable, "offline")
					: IapConfirmResult.Confirmed());

				Purchase(Gem);
				Assert.That(_analytics.Events, Is.Empty);

				Clock.Elapsed += RetryDelay;
				Delay.Complete(RetryDelay);

				Assert.That(_analytics.Events, Has.Count.EqualTo(1));
			}
		}

		[Test]
		public void TransactionClosedElsewhere_IsNotLogged()
		{
			using (new IapRevenueLogger(Service, _analytics))
			{
				Initialize();
				CompletePurchasesImmediately();
				Store.ConfirmHandler = order => UniTask.FromResult(IapConfirmResult.AlreadyConfirmed());

				Purchase(Gem);

				Assert.That(_analytics.Events, Is.Empty);
			}
		}

		[Test]
		public void FirstGrantAcknowledgedByTheServer_IsLoggedOnce()
		{
			// 결제 서버가 Google 결제를 지급하면서 확인하면 클라이언트 확정은 AlreadyConfirmed 로 끝나지만, 이번 지급이 첫 지급이라 매출이다
			using (new IapRevenueLogger(Service, _analytics))
			{
				Initialize();
				CompletePurchasesImmediately();
				Fulfiller.Next(FulfillmentResult.Granted(new IapRedeemResponse<object> { Status = IapRedeemStatus.Granted, Quantity = 1 }));
				Store.ConfirmHandler = order => UniTask.FromResult(IapConfirmResult.AlreadyConfirmed());

				Purchase(Gem);

				Assert.That(_analytics.Events, Has.Count.EqualTo(1));
			}
		}

		[Test]
		public void UnknownCurrency_IsNotLogged()
		{
			Store.ProductsHandler = () => UniTask.FromResult(Result<IReadOnlyList<IapProduct>>.Ok(new[]
			{
				new IapProduct(Gem, "1,100", 1100m, null, "gem", "", true)
			}));

			using (new IapRevenueLogger(Service, _analytics))
			{
				Initialize();
				CompletePurchasesImmediately();

				Purchase(Gem);

				Assert.That(_analytics.Events, Is.Empty);
			}
		}

		[Test]
		public void GivenSinks_AreUsed()
		{
			using (new IapRevenueLogger(Service, _analytics, AnalyticsSinks.Firebase))
			{
				Initialize();
				CompletePurchasesImmediately();

				Purchase(Gem);

				Assert.That(_analytics.Events[0].Sinks, Is.EqualTo(AnalyticsSinks.Firebase));
			}
		}

		[Test]
		public void Subscription_IsNotLoggedByDefault()
		{
			using (new IapRevenueLogger(Service, _analytics))
			{
				Initialize();
				CompletePurchasesImmediately();

				var result = Purchase(Vip);

				Assert.That(result.Value.IsSuccess, Is.True);
				Assert.That(_analytics.Events, Is.Empty, "free trials and introductory prices make the list price wrong");
			}
		}

		[Test]
		public void PaidAmount_FromTheFulfillerResponseIsLogged()
		{
			Fulfiller.Default = (order, token) => UniTask.FromResult(FulfillmentResult.Granted(new IapPaidAmount(0m, "USD")));

			using (new IapRevenueLogger(Service, _analytics, paidAmount: settlement => (IapPaidAmount) settlement.Fulfillment.Response))
			{
				Initialize();
				CompletePurchasesImmediately();

				Purchase(Vip);

				Assert.That(_analytics.Events, Has.Count.EqualTo(1));
				Assert.That(_analytics.Events[0].Parameters[AnalyticsStandardParameters.Price], Is.EqualTo(0d), "a free trial logs zero revenue");
			}
		}

		[Test]
		public void PaidCurrency_FromTheServerIsKeptEvenIfTheStoreShowsAnother()
		{
			// 상품 조회는 KRW 지만 서버가 확인한 결제는 USD 입니다(이전 실행의 결제 복구 등)
			Fulfiller.Default = (order, token) => UniTask.FromResult(FulfillmentResult.Granted(new IapPaidAmount(1.99m, "USD")));

			using (new IapRevenueLogger(Service, _analytics, paidAmount: settlement => (IapPaidAmount) settlement.Fulfillment.Response))
			{
				Initialize();
				CompletePurchasesImmediately();

				Purchase(Gem);

				Assert.That(_analytics.Events[0].Parameters[AnalyticsStandardParameters.Price], Is.EqualTo(1.99d));
				Assert.That(_analytics.Events[0].Parameters[AnalyticsStandardParameters.Currency], Is.EqualTo("USD"));
			}
		}

		[Test]
		public void PaidAmountWithoutACurrency_IsNotLogged()
		{
			using (new IapRevenueLogger(Service, _analytics, paidAmount: settlement => new IapPaidAmount(1m, null)))
			{
				Initialize();
				CompletePurchasesImmediately();
				LogAssert.Expect(LogType.Warning, new Regex("negative or has no currency code"));

				Purchase(Gem);

				Assert.That(_analytics.Events, Is.Empty);
			}
		}

		[Test]
		public void UnknownPaidAmount_IsNotLogged()
		{
			using (new IapRevenueLogger(Service, _analytics, paidAmount: settlement => null))
			{
				Initialize();
				CompletePurchasesImmediately();

				Purchase(Gem);

				Assert.That(_analytics.Events, Is.Empty);
			}
		}

		[Test]
		public void Dispose_StopsLogging()
		{
			new IapRevenueLogger(Service, _analytics).Dispose();
			Initialize();
			CompletePurchasesImmediately();

			Purchase(Gem);

			Assert.That(_analytics.Events, Is.Empty);
		}

		private sealed class RecordingAnalytics : IAnalyticsService
		{
			public List<(string Name, IReadOnlyDictionary<string, object> Parameters, AnalyticsSinks Sinks)> Events { get; } =
				new List<(string, IReadOnlyDictionary<string, object>, AnalyticsSinks)>();

			public AnalyticsSinks DefaultSinks => AnalyticsSinks.Console | AnalyticsSinks.Collector;

			public string UserId => null;

			public IReadOnlyDictionary<string, string> UserProperties => new Dictionary<string, string>();

			public void LogEvent(string name, IReadOnlyDictionary<string, object> parameters = null)
			{
				LogEvent(name, parameters, DefaultSinks);
			}

			public void LogEvent(string name, IReadOnlyDictionary<string, object> parameters, AnalyticsSinks sinks)
			{
				Events.Add((name, parameters, sinks));
			}

			public void SetUserId(string userId)
			{
			}

			public void SetUserProperty(string name, string value)
			{
			}

			public void AddSink(IAnalyticsSink sink)
			{
				throw new NotSupportedException();
			}

			public void Dispose()
			{
			}
		}
	}
}
