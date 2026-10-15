using System;
using System.Collections.Generic;
using Geuneda.Core;
using NUnit.Framework;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Tests
{
	/// <summary>
	/// 가짜 스토어·지급자·지연으로 서비스를 만듭니다. 가짜가 바로 끝나는 UniTask 를 돌려주므로 대부분의 흐름이 호출 안에서 끝납니다.
	/// 시간 설정은 길이마다 달라 <see cref="ManualDelay.Complete"/> 로 하나만 골라 끝낼 수 있습니다.
	/// </summary>
	public abstract class IapServiceTestBase
	{
		protected static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(31);
		protected static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);
		protected static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(180);
		protected static readonly TimeSpan FulfillTimeout = TimeSpan.FromSeconds(30);
		protected static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(14);
		protected static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
		protected static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(40);

		protected static readonly IapProductDefinition Gem = new IapProductDefinition("gem_100", IapProductType.Consumable);
		protected static readonly IapProductDefinition Coin = new IapProductDefinition("coin_100", IapProductType.Consumable);
		protected static readonly IapProductDefinition NoAds = new IapProductDefinition("no_ads", IapProductType.NonConsumable, "com.game.noads");
		protected static readonly IapProductDefinition Vip = new IapProductDefinition("vip", IapProductType.Subscription);

		protected FakeStore Store;
		protected ScriptedFulfiller Fulfiller;
		protected ManualDelay Delay;
		protected FakeMonotonicClock Clock;
		protected IapService Service;
		protected List<PurchaseSettlement> Settled;
		protected List<IapOrderFailure> Failures;
		protected List<PurchaseSettlement> Confirmed;
		protected List<bool> StoreUi;

		[SetUp]
		public void SetUpService()
		{
			Store = new FakeStore();
			Fulfiller = new ScriptedFulfiller();
			Delay = new ManualDelay();
			Clock = new FakeMonotonicClock();
			Service = Create();
		}

		[TearDown]
		public void TearDownService()
		{
			Service?.Dispose();
		}

		protected IapService Create(Action<IapServiceOptions> configure = null)
		{
			var options = new IapServiceOptions
			{
				Products = new[] { Gem, Coin, NoAds, Vip },
				ConnectTimeout = ConnectTimeout,
				FetchTimeout = FetchTimeout,
				StoreResponseTimeout = StoreTimeout,
				FulfillTimeout = FulfillTimeout,
				ConfirmTimeout = ConfirmTimeout,
				RetryDelay = RetryDelay,
				MaxRetryDelay = MaxRetryDelay
			};

			configure?.Invoke(options);
			// 무작위 값 1 에 가깝게 해 백오프가 정확히 RetryDelay, 두 배, ... 가 되게 합니다
			var service = new IapService(Store, Fulfiller, options, Clock, Delay.Delay, () => 1.0, false);
			Settled = new List<PurchaseSettlement>();
			Failures = new List<IapOrderFailure>();
			Confirmed = new List<PurchaseSettlement>();
			StoreUi = new List<bool>();
			service.OrderSettled += Settled.Add;
			service.OrderFailed += Failures.Add;
			service.PurchaseConfirmed += Confirmed.Add;
			service.StoreUiChanged += StoreUi.Add;
			return service;
		}

		protected void Initialize()
		{
			var initialized = Captured<Result>.Of(Service.InitializeAsync());
			Assert.That(initialized.Done, Is.True, "initialization should finish with an immediate fake store");
			Assert.That(initialized.Value.IsSuccess, Is.True, initialized.Value.ToString());
		}

		protected Captured<Result<PurchaseSettlement>> Purchase(IapProductDefinition product, string payload = null)
		{
			return Captured<Result<PurchaseSettlement>>.Of(Service.PurchaseAsync(product.Id, payload));
		}

		/// <summary>결제창을 열면 바로 결제를 끝내는 스토어로 바꿉니다.</summary>
		protected void CompletePurchasesImmediately()
		{
			Store.OnPurchase = definition => Store.CompletePurchase(definition.Id);
		}
	}
}
