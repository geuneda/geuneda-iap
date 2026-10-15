using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>Android Publisher API 가짜입니다. 구매 토큰·주문 ID 마다 응답을 정하고 받은 요청을 기록합니다. 정하지 않은 토큰은 404 입니다.</summary>
	internal sealed class FakeGooglePlay : HttpMessageHandler
	{
		public const string Package = "com.test.game";
		public const string AccessToken = "test-access-token";

		public ConcurrentDictionary<string, (HttpStatusCode Status, string Json)> Products { get; } = new ConcurrentDictionary<string, (HttpStatusCode, string)>();
		public ConcurrentDictionary<string, (HttpStatusCode Status, string Json)> Subscriptions { get; } = new ConcurrentDictionary<string, (HttpStatusCode, string)>();
		public ConcurrentDictionary<string, (HttpStatusCode Status, string Json)> Orders { get; } = new ConcurrentDictionary<string, (HttpStatusCode, string)>();
		public ConcurrentQueue<(string Method, string Path, string Authorization)> Requests { get; } = new ConcurrentQueue<(string, string, string)>();

		public HttpStatusCode AcknowledgeStatus { get; set; } = HttpStatusCode.OK;

		/// <summary>구매 토큰별 확인 응답(없으면 <see cref="AcknowledgeStatus"/>)</summary>
		public ConcurrentDictionary<string, HttpStatusCode> AcknowledgeStatuses { get; } = new ConcurrentDictionary<string, HttpStatusCode>();

		/// <summary>구독 토큰별로 조회마다 하나씩 꺼내 쓰는 응답(지연 포함). 비면 <see cref="Subscriptions"/></summary>
		public ConcurrentDictionary<string, ConcurrentQueue<(TimeSpan Delay, string Json)>> SubscriptionScripts { get; } =
			new ConcurrentDictionary<string, ConcurrentQueue<(TimeSpan Delay, string Json)>>();

		/// <summary>있으면 요청마다 이 예외를 던집니다(연결 실패)</summary>
		public Exception Throw { get; set; }

		/// <summary>있으면 응답 전에 기다립니다(시간 초과)</summary>
		public TimeSpan? Delay { get; set; }

		public int Count(string pathPart)
		{
			var count = 0;

			foreach (var request in Requests)
			{
				if (request.Path.Contains(pathPart, StringComparison.Ordinal))
				{
					count++;
				}
			}

			return count;
		}

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
			Requests.Enqueue((request.Method.Method, path, request.Headers.Authorization?.ToString()));

			if (Delay != null)
			{
				await Task.Delay(Delay.Value, cancellationToken);
			}

			if (Throw != null)
			{
				throw Throw;
			}

			var prefix = $"/androidpublisher/v3/applications/{Package}/";

			if (!path.StartsWith(prefix, StringComparison.Ordinal))
			{
				return Json(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"wrong package\"}}");
			}

			var rest = path.Substring(prefix.Length);

			if (rest.EndsWith(":acknowledge", StringComparison.Ordinal))
			{
				var token = rest.Substring(rest.IndexOf("/tokens/", StringComparison.Ordinal) + "/tokens/".Length).Replace(":acknowledge", "");
				return Json(AcknowledgeStatuses.TryGetValue(token, out var status) ? status : AcknowledgeStatus, "");
			}

			const string subscriptionRoute = "purchases/subscriptionsv2/tokens/";

			if (rest.StartsWith(subscriptionRoute, StringComparison.Ordinal) && SubscriptionScripts.TryGetValue(rest.Substring(subscriptionRoute.Length), out var script) &&
			    script.TryDequeue(out var scripted))
			{
				await Task.Delay(scripted.Delay, cancellationToken);
				return Json(HttpStatusCode.OK, scripted.Json);
			}

			return Lookup(rest, "purchases/productsv2/tokens/", Products) ?? Lookup(rest, subscriptionRoute, Subscriptions) ??
			       Lookup(rest, "orders/", Orders) ?? Json(HttpStatusCode.NotFound, "{}");
		}

		private static HttpResponseMessage Lookup(string rest, string route, ConcurrentDictionary<string, (HttpStatusCode Status, string Json)> table)
		{
			if (!rest.StartsWith(route, StringComparison.Ordinal))
			{
				return null;
			}

			return table.TryGetValue(rest.Substring(route.Length), out var entry)
				? Json(entry.Status, entry.Json)
				: Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":404,\"message\":\"The purchase token was not found.\"}}");
		}

		private static HttpResponseMessage Json(HttpStatusCode status, string json)
		{
			return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
		}

		public static string Product(string productId, string state = "PURCHASED", string ack = "ACKNOWLEDGEMENT_STATE_PENDING", int quantity = 1,
			bool test = false, string orderId = "GPA.1234-5678-9012-34567", string account = null)
		{
			var testContext = test ? ",\"testPurchaseContext\":{\"fopType\":\"TEST\"}" : "";
			var accountField = account == null ? "" : $",\"obfuscatedExternalAccountId\":\"{account}\"";
			return "{\"kind\":\"androidpublisher#productPurchaseV2\"," +
			       $"\"productLineItem\":[{{\"productId\":\"{productId}\",\"productOfferDetails\":{{\"quantity\":{quantity},\"consumptionState\":\"CONSUMPTION_STATE_YET_TO_BE_CONSUMED\"}}}}]," +
			       $"\"purchaseStateContext\":{{\"purchaseState\":\"{state}\"}},\"orderId\":\"{orderId}\",\"regionCode\":\"KR\"," +
			       $"\"purchaseCompletionTime\":\"2026-10-15T01:02:03.123456789Z\",\"acknowledgementState\":\"{ack}\"{testContext}{accountField}}}";
		}

		public static string Subscription(string productId, string state = "SUBSCRIPTION_STATE_ACTIVE", string ack = "ACKNOWLEDGEMENT_STATE_PENDING",
			string expiry = "2026-11-15T00:00:00Z", bool test = false, string orderId = "GPA.1111-2222-3333-44444..0")
		{
			var testField = test ? ",\"testPurchase\":{}" : "";
			return "{\"kind\":\"androidpublisher#subscriptionPurchaseV2\",\"regionCode\":\"KR\",\"startTime\":\"2026-10-15T00:00:00Z\"," +
			       $"\"lineItems\":[{{\"productId\":\"{productId}\",\"expiryTime\":\"{expiry}\",\"latestSuccessfulOrderId\":\"{orderId}\",\"autoRenewingPlan\":{{\"autoRenewEnabled\":true}}}}]," +
			       $"\"subscriptionState\":\"{state}\",\"acknowledgementState\":\"{ack}\"," +
			       $"\"externalAccountIdentifiers\":{{\"obfuscatedExternalAccountId\":\"acc-hash\"}}{testField}}}";
		}
	}

	internal sealed class FakeTokenSource : IGooglePlayAccessTokenSource
	{
		public Exception Throw { get; set; }

		public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
		{
			return Throw != null ? Task.FromException<string>(Throw) : Task.FromResult(FakeGooglePlay.AccessToken);
		}
	}
}
