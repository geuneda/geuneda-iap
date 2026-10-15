using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using Geuneda.Http;
using Geuneda.Iap.Http;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Tests.Http
{
	/// <summary>HTTP 지급자가 서버 응답을 지급 결과로 바꾸는 규칙(확실하지 않으면 거래를 열어 둠)을 확인합니다.</summary>
	[TestFixture]
	public class HttpPurchaseFulfillerTest
	{
		private FakeTransport _transport;
		private HttpPurchaseFulfiller<Reward> _fulfiller;

		[SetUp]
		public void SetUp()
		{
			_transport = new FakeTransport();
			var http = new HttpClientBuilder().WithBaseAddress("https://game.example.com/iap/").WithTransport(_transport).Build();
			_fulfiller = new HttpPurchaseFulfiller<Reward>(http);
		}

		internal static IapOrder Order(string payload = "reward-2")
		{
			var definition = new IapProductDefinition("gem_100", IapProductType.Consumable, "com.game.gem100");
			var product = new IapProduct(definition, "₩1,200", 1200m, "KRW", "Gems", "", true);
			var storeOrder = new IapStoreOrder("gem_100", "tx-1", "{\"Store\":\"AppleAppStore\"}", true, null, "header.payload.signature", "tx-0");
			return new IapOrder(definition, "gem_100", product, storeOrder, IapStoreNames.AppleAppStore, IapOrderOrigin.Purchase, payload);
		}

		[UnityTest]
		public IEnumerator Granted_response_becomes_granted_with_the_typed_grant() => UniTask.ToCoroutine(async () =>
		{
			_transport.Respond = request => FakeTransport.Reply(request, 200,
				"{\"status\":1,\"code\":0,\"grant\":{\"gems\":100},\"productId\":\"gem_100\",\"transactionId\":\"tx-1\",\"quantity\":1," +
				"\"isTest\":true,\"paidAmount\":1200.5,\"paidCurrency\":\"KRW\",\"expiresAt\":\"2026-11-15T00:00:00+00:00\"}");

			var result = await _fulfiller.FulfillAsync(Order(), CancellationToken.None);

			Assert.AreEqual(FulfillmentStatus.Granted, result.Status);
			var response = (IapRedeemResponse<Reward>) result.Response;
			Assert.AreEqual(100, response.Grant.Gems);
			Assert.AreEqual(1200.5m, response.PaidAmount);
			Assert.AreEqual("KRW", response.PaidCurrency);
			Assert.IsTrue(response.IsTest);
			Assert.AreEqual(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero), response.ExpiresAt);
		});

		[UnityTest]
		public IEnumerator Request_carries_the_order_in_camel_case() => UniTask.ToCoroutine(async () =>
		{
			_transport.Respond = request => FakeTransport.Reply(request, 200, "{\"status\":1}");

			await _fulfiller.FulfillAsync(Order(), CancellationToken.None);

			var request = _transport.Requests[0];
			Assert.AreEqual(HttpMethod.Post, request.Method);
			Assert.AreEqual("https://game.example.com/iap/purchases", request.Uri.AbsoluteUri);
			var body = JObject.Parse(Encoding.UTF8.GetString(request.Body));
			Assert.AreEqual(IapStoreNames.AppleAppStore, (string) body["store"]);
			Assert.AreEqual("gem_100", (string) body["productId"]);
			Assert.AreEqual("tx-1", (string) body["transactionId"]);
			Assert.AreEqual("{\"Store\":\"AppleAppStore\"}", (string) body["receipt"]);
			Assert.AreEqual("header.payload.signature", (string) body["signedTransaction"]);
			Assert.AreEqual("reward-2", (string) body["payload"]);
		});

		[UnityTest]
		public IEnumerator Null_payload_is_left_out() => UniTask.ToCoroutine(async () =>
		{
			_transport.Respond = request => FakeTransport.Reply(request, 200, "{\"status\":1}");

			await _fulfiller.FulfillAsync(Order(null), CancellationToken.None);

			Assert.IsFalse(JObject.Parse(Encoding.UTF8.GetString(_transport.Requests[0].Body)).ContainsKey("payload"));
		});

		[UnityTest]
		public IEnumerator Server_statuses_map_to_fulfillment_statuses() => UniTask.ToCoroutine(async () =>
		{
			var cases = new[]
			{
				(200, "{\"status\":2,\"code\":7101,\"grant\":{\"gems\":100}}", FulfillmentStatus.AlreadyGranted),
				(200, "{\"status\":2,\"code\":7102}", FulfillmentStatus.AlreadyGranted),
				(200, "{\"status\":3,\"code\":7110,\"message\":\"store down\"}", FulfillmentStatus.RetryLater),
				(200, "{\"status\":4,\"code\":7104}", FulfillmentStatus.Rejected),
				(400, "{\"status\":4,\"code\":7113}", FulfillmentStatus.Rejected),
				(401, "{\"status\":3,\"code\":7114}", FulfillmentStatus.RetryLater)
			};

			foreach (var (status, json, expected) in cases)
			{
				_transport.Respond = request => FakeTransport.Reply(request, status, json);
				var result = await _fulfiller.FulfillAsync(Order(), CancellationToken.None);
				Assert.AreEqual(expected, result.Status, json);
			}
		});

		[UnityTest]
		public IEnumerator Duplicate_keeps_the_first_grant_and_the_reason() => UniTask.ToCoroutine(async () =>
		{
			_transport.Respond = request => FakeTransport.Reply(request, 200, "{\"status\":2,\"code\":7101,\"message\":\"Already granted\",\"grant\":{\"gems\":100}}");

			var result = await _fulfiller.FulfillAsync(Order(), CancellationToken.None);

			Assert.AreEqual(100, ((IapRedeemResponse<Reward>) result.Response).Grant.Gems);
			StringAssert.Contains("7101", result.Message);
		});

		[UnityTest]
		public IEnumerator Uncertain_answers_keep_the_transaction_open() => UniTask.ToCoroutine(async () =>
		{
			var answers = new List<Func<HttpRequest, Result<HttpResponse>>>
			{
				request => FakeTransport.Reply(request, 500, "{\"status\":1}"),
				request => FakeTransport.Reply(request, 502, "<html>bad gateway</html>"),
				request => FakeTransport.Reply(request, 200, "not json"),
				request => FakeTransport.Reply(request, 200, "null"),
				request => FakeTransport.Reply(request, 200, "{\"status\":9}"),
				request => FakeTransport.Reply(request, 200, "{}"),
				request => FakeTransport.Reply(request, 413, "{\"error\":\"too large\"}"),
				_ => Result<HttpResponse>.Fail(ResultCodes.NetworkError, "offline"),
				_ => Result<HttpResponse>.Fail(ResultCodes.Timeout, "slow")
			};

			foreach (var answer in answers)
			{
				_transport.Respond = answer;
				var result = await _fulfiller.FulfillAsync(Order(), CancellationToken.None);
				Assert.AreEqual(FulfillmentStatus.RetryLater, result.Status, result.ToString());
			}
		});

		[Test]
		public void Constructor_rejects_misuse()
		{
			var http = new HttpClientBuilder().WithBaseAddress("https://game.example.com/").WithTransport(_transport).Build();

			Assert.Throws<ArgumentNullException>(() => new HttpPurchaseFulfiller<Reward>(null));
			Assert.Throws<ArgumentException>(() => new HttpPurchaseFulfiller<Reward>(http, " "));
			Assert.Throws<ArgumentNullException>(() => ((IapOrder) null).ToRedeemRequest());
		}

		[Test]
		public void Null_response_retries_later()
		{
			Assert.AreEqual(FulfillmentStatus.RetryLater, ((IapRedeemResponse<Reward>) null).ToFulfillmentResult().Status);
		}

		public sealed class Reward
		{
			public int Gems { get; set; }
		}

		internal sealed class FakeTransport : IHttpTransport
		{
			public readonly List<HttpRequest> Requests = new List<HttpRequest>();
			public Func<HttpRequest, Result<HttpResponse>> Respond = request => Reply(request, 200, "{}");

			public UniTask<Result<HttpResponse>> SendAsync(HttpRequest request, CancellationToken cancellationToken)
			{
				Requests.Add(request);
				return UniTask.FromResult(Respond(request));
			}

			public static Result<HttpResponse> Reply(HttpRequest request, int statusCode, string body)
			{
				return Result<HttpResponse>.Ok(new HttpResponse(request.Uri, statusCode, Encoding.UTF8.GetBytes(body), null));
			}
		}
	}
}
