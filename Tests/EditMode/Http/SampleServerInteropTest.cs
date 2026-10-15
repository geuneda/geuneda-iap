using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using Geuneda.Http;
using Geuneda.Iap.Http;
using NUnit.Framework;
using UnityEngine.TestTools;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Tests.Http
{
	/// <summary>
	/// 실제 .NET 샘플 결제 서버(<c>Server~/samples/Geuneda.Iap.SampleServer</c>)와의 상호 운용을 확인합니다(가짜 스토어 주문).
	/// 서버를 띄우고 <c>GEUNEDA_IAP_INTEROP_URL</c>(예: <c>http://127.0.0.1:5499/iap/</c>)을 설정한 뒤 이름으로 골라 실행합니다.
	/// </summary>
	[TestFixture]
	[Explicit("샘플 결제 서버가 필요합니다")]
	[Category("Interop")]
	public class SampleServerInteropTest
	{
		private const string UrlVariable = "GEUNEDA_IAP_INTEROP_URL";

		private static HttpPurchaseFulfiller<SampleGrant> Fulfiller(string account)
		{
			var url = Environment.GetEnvironmentVariable(UrlVariable);

			if (string.IsNullOrEmpty(url))
			{
				Assert.Ignore($"{UrlVariable} is not set");
			}

			var http = new HttpClientBuilder().WithBaseAddress(url.TrimEnd('/') + "/").AddMiddleware(new AccountHeader(account)).Build();
			return new HttpPurchaseFulfiller<SampleGrant>(http);
		}

		private static IapOrder FakeOrder(string transactionId)
		{
			var definition = new IapProductDefinition("gem_100", IapProductType.Consumable);
			var storeOrder = new IapStoreOrder("gem_100", transactionId, "{}", true, null);
			return new IapOrder(definition, "gem_100", null, storeOrder, IapStoreNames.Fake, IapOrderOrigin.Purchase, "reward-1");
		}

		[UnityTest]
		public IEnumerator SampleServer_GrantsOnceAndAnswersDuplicates() => UniTask.ToCoroutine(async () =>
		{
			var account = "interop-" + Guid.NewGuid().ToString("N");
			var order = FakeOrder("interop-tx-" + Guid.NewGuid().ToString("N"));
			var fulfiller = Fulfiller(account);

			var granted = await fulfiller.FulfillAsync(order, CancellationToken.None);
			var duplicate = await fulfiller.FulfillAsync(order, CancellationToken.None);
			var stolen = await Fulfiller(account + "-other").FulfillAsync(order, CancellationToken.None);

			Assert.AreEqual(FulfillmentStatus.Granted, granted.Status, granted.ToString());
			var grant = ((IapRedeemResponse<SampleGrant>) granted.Response).Grant;
			Assert.AreEqual("gem_100", grant.Product);
			Assert.AreEqual(100, grant.Gems);
			Assert.AreEqual(100, grant.Balance);
			Assert.IsTrue(((IapRedeemResponse<SampleGrant>) granted.Response).IsTest);

			Assert.AreEqual(FulfillmentStatus.AlreadyGranted, duplicate.Status);
			var first = (IapRedeemResponse<SampleGrant>) duplicate.Response;
			Assert.AreEqual(IapRedeemCodes.Duplicate, first.Code);
			Assert.AreEqual(100, first.Grant.Balance, "the duplicate answers the first grant");

			Assert.AreEqual(FulfillmentStatus.AlreadyGranted, stolen.Status);
			Assert.AreEqual(IapRedeemCodes.OwnedByOtherAccount, ((IapRedeemResponse<SampleGrant>) stolen.Response).Code);
			Assert.IsNull(((IapRedeemResponse<SampleGrant>) stolen.Response).Grant);
		});

		[UnityTest]
		public IEnumerator SampleServer_RefusesMissingAccountAndUnknownProduct() => UniTask.ToCoroutine(async () =>
		{
			var anonymous = await Fulfiller(null).FulfillAsync(FakeOrder("interop-tx-" + Guid.NewGuid().ToString("N")), CancellationToken.None);
			var definition = new IapProductDefinition("gem_999", IapProductType.Consumable);
			var unknown = new IapOrder(definition, "gem_999", null, new IapStoreOrder("gem_999", "interop-tx-" + Guid.NewGuid().ToString("N"), "{}", true, null),
				IapStoreNames.Fake, IapOrderOrigin.Recovered, null);
			var rejected = await Fulfiller("interop-account").FulfillAsync(unknown, CancellationToken.None);

			Assert.AreEqual(FulfillmentStatus.RetryLater, anonymous.Status, anonymous.ToString());
			StringAssert.Contains(IapRedeemCodes.Unauthorized.ToString(), anonymous.Message);
			Assert.AreEqual(FulfillmentStatus.Rejected, rejected.Status);
			StringAssert.Contains(IapRedeemCodes.ProductMismatch.ToString(), rejected.Message);
		});

		public sealed class SampleGrant
		{
			public string Product { get; set; }
			public int Gems { get; set; }
			public int Balance { get; set; }
		}

		private sealed class AccountHeader : IHttpMiddleware
		{
			private readonly string _account;

			public AccountHeader(string account)
			{
				_account = account;
			}

			public UniTask<Result<HttpResponse>> SendAsync(HttpRequest request, HttpHandler next, CancellationToken cancellationToken)
			{
				return next(_account == null ? request : request.WithHeader("X-Sample-Account", _account), cancellationToken);
			}
		}
	}
}
