using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>지급 흐름(원장, 중복, 지급자 결과, 요청 검사)을 가짜 스토어로 확인합니다.</summary>
	public sealed class RedeemTest
	{
		private static Task<IapTestServer> StartAsync(Action<IapServerOptions> configure = null)
		{
			return IapTestServer.StartAsync(iap => iap.AddFakeStore(), configure);
		}

		[Fact]
		public async Task Grants_once_and_records_the_grant_response()
		{
			await using var server = await StartAsync();
			var (status, body) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1", payload: "reward-2"));

			Assert.Equal(200, status);
			Assert.Equal(IapRedeemStatus.Granted, body.Status());
			Assert.Equal(IapRedeemCodes.None, body.Code());
			Assert.Equal(100, body.GetProperty("grant").GetProperty("gems").GetInt32());
			Assert.Equal("gem_100", body.GetProperty("productId").GetString());
			Assert.True(body.GetProperty("isTest").GetBoolean());
			Assert.False(body.Has("paidAmount"));

			var grant = Assert.Single(server.Grantor.Grants);
			Assert.Equal(IapTestServer.DefaultAccount, grant.AccountId);
			Assert.Equal("reward-2", grant.Payload);

			var record = await server.Ledger.FindAsync(IapStoreNames.Fake, "tx-1", CancellationToken.None);
			Assert.Equal(IapTestServer.DefaultAccount, record.AccountId);
			Assert.Equal(IapTestServer.Now, record.RedeemedAt);
			Assert.Contains("\"gems\":100", record.GrantResponse);
		}

		[Fact]
		public async Task Ledger_retry_of_the_grant_callback_does_not_keep_the_first_attempts_refund()
		{
			// DB 원장은 커밋 충돌 때 롤백하고 콜백을 다시 부른다. 첫 시도가 본 '모두 환불'이, 다시 부른 시도(그사이 환불 취소)의 지급 미룸을
			// Revoked 로 바꿔 클라이언트가 거래를 닫게 하지 않는다(리뷰 2b 4회차 — 시도마다 초기화를 빼면 Revoked 로 실패)
			await using var server = await IapTestServer.StartAsync(iap => iap.AddFakeStore().AddLedger<HookedLedger>(ServiceLifetime.Singleton));
			var ledger = (HookedLedger) server.Services.GetRequiredService<IPurchaseLedger>();
			ledger.FirstAttemptPreGrant = new PurchaseRecord
			{
				Store = IapStoreNames.Fake, TransactionId = "tx-1", AccountId = null, ProductId = "gem_100", Quantity = 1, RefundedQuantity = 1
			};
			server.Grantor.Enqueue(GrantResult.RetryLater("inventory is busy"));

			var (_, deferred) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1"));
			var (_, granted) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1"));

			Assert.Equal(IapRedeemStatus.RetryLater, deferred.Status());
			Assert.Equal(IapRedeemCodes.GrantDeferred, deferred.Code());
			Assert.Equal(IapRedeemStatus.Granted, granted.Status());
			Assert.Equal(2, server.Grantor.Calls);
		}

		[Fact]
		public async Task Same_account_duplicate_returns_the_first_grant_without_granting_again()
		{
			await using var server = await StartAsync();
			var request = IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1");
			await server.RedeemAsync(request);

			var (status, body) = await server.RedeemAsync(request);

			Assert.Equal(200, status);
			Assert.Equal(IapRedeemStatus.AlreadyGranted, body.Status());
			Assert.Equal(IapRedeemCodes.Duplicate, body.Code());
			Assert.Equal(100, body.GetProperty("grant").GetProperty("gems").GetInt32());
			Assert.Equal(1, server.Grantor.Calls);
		}

		[Fact]
		public async Task Other_account_gets_owned_by_other_account_without_the_grant()
		{
			await using var server = await StartAsync();
			var request = IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1");
			await server.RedeemAsync(request);

			var (status, body) = await server.RedeemAsync(request, "account-2");

			Assert.Equal(200, status);
			Assert.Equal(IapRedeemStatus.AlreadyGranted, body.Status());
			Assert.Equal(IapRedeemCodes.OwnedByOtherAccount, body.Code());
			Assert.False(body.Has("grant"));
			Assert.Equal(1, server.Grantor.Calls);
		}

		[Fact]
		public async Task Concurrent_requests_for_one_transaction_grant_once()
		{
			await using var server = await StartAsync();
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			server.Grantor.Enqueue(async (grant, token) =>
			{
				entered.SetResult(true);
				await release.Task;
				return GrantResult.Granted(new TestReward { Gems = 1 });
			});
			var request = IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1");

			var first = server.RedeemAsync(request);
			await entered.Task;
			var second = server.RedeemAsync(request);
			await Task.Delay(200);
			release.SetResult(true);

			var results = await Task.WhenAll(first, second);

			Assert.Equal(1, server.Grantor.Calls);
			Assert.Equal(IapRedeemStatus.Granted, results[0].Body.Status());
			Assert.Equal(IapRedeemCodes.Duplicate, results[1].Body.Code());
			Assert.Equal(1, results[1].Body.GetProperty("grant").GetProperty("gems").GetInt32());
		}

		[Fact]
		public async Task Grantor_rejection_is_not_recorded_and_the_next_attempt_grants()
		{
			await using var server = await StartAsync();
			server.Grantor.Enqueue(GrantResult.Rejected("limit reached"));
			var request = IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1");

			var (_, rejected) = await server.RedeemAsync(request);
			var (_, granted) = await server.RedeemAsync(request);

			Assert.Equal(IapRedeemStatus.Rejected, rejected.Status());
			Assert.Equal(IapRedeemCodes.GrantRejected, rejected.Code());
			Assert.Equal("limit reached", rejected.GetProperty("message").GetString());
			Assert.Equal(IapRedeemStatus.Granted, granted.Status());
			Assert.Equal(2, server.Grantor.Calls);
		}

		[Fact]
		public async Task Grantor_retry_later_is_not_recorded()
		{
			await using var server = await StartAsync();
			server.Grantor.Enqueue(GrantResult.RetryLater("db busy"));

			var (status, body) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1"));

			Assert.Equal(200, status);
			Assert.Equal(IapRedeemStatus.RetryLater, body.Status());
			Assert.Equal(IapRedeemCodes.GrantDeferred, body.Code());
			Assert.Null(await server.Ledger.FindAsync(IapStoreNames.Fake, "tx-1", CancellationToken.None));
		}

		[Fact]
		public async Task Grantor_exception_is_a_server_error_and_nothing_is_recorded()
		{
			await using var server = await StartAsync();
			server.Grantor.Enqueue((_, _) => Task.FromException<GrantResult>(new InvalidOperationException("bug")));
			var request = IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1");

			var (status, _) = await server.RedeemAsync(request);
			var (_, retried) = await server.RedeemAsync(request);

			Assert.Equal(500, status);
			Assert.Equal(IapRedeemStatus.Granted, retried.Status());
		}

		[Fact]
		public async Task Undefined_grant_status_is_a_server_error()
		{
			await using var server = await StartAsync();
			server.Grantor.Enqueue(default(GrantResult));

			var (status, _) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1"));

			Assert.Equal(500, status);
			Assert.Null(await server.Ledger.FindAsync(IapStoreNames.Fake, "tx-1", CancellationToken.None));
		}

		[Fact]
		public async Task Test_purchases_are_rejected_when_not_allowed()
		{
			await using var server = await StartAsync(options => options.AllowTestPurchases = false);

			var (_, body) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1"));

			Assert.Equal(IapRedeemStatus.Rejected, body.Status());
			Assert.Equal(IapRedeemCodes.EnvironmentNotAllowed, body.Code());
			Assert.Equal(0, server.Grantor.Calls);
		}

		[Fact]
		public async Task Unknown_product_and_store_are_rejected()
		{
			await using var server = await StartAsync();

			var (_, product) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.Fake, "gem_999", "tx-1"));
			var (_, store) = await server.RedeemAsync(IapTestServer.Request("OneStore", "gem_100", "tx-1"));

			Assert.Equal(IapRedeemCodes.ProductMismatch, product.Code());
			Assert.Equal(IapRedeemStatus.Rejected, product.Status());
			Assert.Equal(IapRedeemCodes.UnsupportedStore, store.Code());
			Assert.Equal(0, server.Grantor.Calls);
		}

		[Fact]
		public async Task Missing_fields_are_a_bad_request()
		{
			await using var server = await StartAsync();

			var (status, body) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.Fake, "gem_100", " "));

			Assert.Equal(400, status);
			Assert.Equal(IapRedeemCodes.BadRequest, body.Code());
		}

		[Fact]
		public async Task Missing_account_is_unauthorized()
		{
			await using var server = await StartAsync();

			var (status, body) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.Fake, "gem_100", "tx-1"), account: null);

			Assert.Equal(401, status);
			Assert.Equal(IapRedeemStatus.RetryLater, body.Status());
			Assert.Equal(IapRedeemCodes.Unauthorized, body.Code());
			Assert.Equal(0, server.Grantor.Calls);
		}

		[Fact]
		public async Task Malformed_bodies_are_refused()
		{
			await using var server = await StartAsync(options => options.MaxRequestBytes = 64);

			var text = await PostRawAsync(server, "{}", "text/plain");
			var invalid = await PostRawAsync(server, "{not json", "application/json");
			var nullBody = await PostRawAsync(server, "null", "application/json");
			var large = await PostRawAsync(server, "{\"receipt\":\"" + new string('a', 100) + "\"}", "application/json");

			Assert.Equal(415, text);
			Assert.Equal(400, invalid);
			Assert.Equal(400, nullBody);
			Assert.Equal(413, large);
		}

		[Fact]
		public async Task Response_names_are_camel_case_and_status_is_a_number()
		{
			await using var server = await StartAsync();
			using var message = new HttpRequestMessage(HttpMethod.Post, IapRoutes.Purchases)
			{
				Content = new StringContent("{\"Store\":\"fake\",\"productId\":\"gem_100\",\"TRANSACTIONID\":\"tx-1\"}", Encoding.UTF8, "application/json")
			};
			message.Headers.Add(IapTestServer.AccountHeader, IapTestServer.DefaultAccount);

			using var response = await server.Http.SendAsync(message);
			var json = await response.Content.ReadAsStringAsync();

			Assert.Contains("\"status\":1", json);
			Assert.Contains("\"transactionId\":\"tx-1\"", json);
			Assert.DoesNotContain("\"Status\"", json);
		}

		private static async Task<int> PostRawAsync(IapTestServer server, string body, string contentType)
		{
			using var message = new HttpRequestMessage(HttpMethod.Post, IapRoutes.Purchases) { Content = new StringContent(body, Encoding.UTF8, contentType) };
			message.Headers.Add(IapTestServer.AccountHeader, IapTestServer.DefaultAccount);
			using var response = await server.Http.SendAsync(message);
			return (int) response.StatusCode;
		}
	}
}
