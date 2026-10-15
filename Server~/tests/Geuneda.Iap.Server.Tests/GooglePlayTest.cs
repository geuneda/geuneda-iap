using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>Google Play 검증기를 가짜 Android Publisher API 로 확인합니다.</summary>
	public sealed class GooglePlayTest
	{
		private readonly FakeGooglePlay _google = new FakeGooglePlay();
		private readonly FakeTokenSource _tokens = new FakeTokenSource();

		private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

		private async Task<IapTestServer> StartAsync(Action<GooglePlayOptions> configure = null)
		{
			var server = await IapTestServer.StartAsync(iap => iap.AddGooglePlay(options =>
			{
				options.PackageName = FakeGooglePlay.Package;
				options.RequestTimeout = TimeSpan.FromSeconds(1);
				configure?.Invoke(options);
			}), options => options.AcknowledgementRetryInterval = RetryInterval, services =>
			{
				services.AddSingleton<IGooglePlayAccessTokenSource>(_tokens);
				services.AddHttpClient(GooglePlayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _google);
			});

			// 호스트의 작업자 루프는 멈추고 테스트가 RunOnceAsync 로 직접 돌린다(시간을 앞당겨도 끼어들지 않게)
			await Worker(server).StopAsync(CancellationToken.None);
			return server;
		}

		private static IapRedeemRequest Request(string productId, string token)
		{
			return IapTestServer.Request(IapStoreNames.GooglePlay, productId, token);
		}

		private static PendingAcknowledgementWorker Worker(IapTestServer server)
		{
			return server.Services.GetServices<IHostedService>().OfType<PendingAcknowledgementWorker>().Single();
		}

		private static async Task<bool> PendingAsync(IapTestServer server, string token)
		{
			return (await server.Ledger.FindAsync(IapStoreNames.GooglePlay, token, CancellationToken.None)).AcknowledgementDueAt != null;
		}

		[Fact]
		public async Task Purchased_product_is_granted_then_acknowledged()
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100", quantity: 3, test: true, account: "acc-hash"));
			await using var server = await StartAsync();

			var (_, body) = await server.RedeemAsync(Request("gem_100", "token-1"));

			Assert.Equal(IapRedeemStatus.Granted, body.Status());
			Assert.Equal(300, body.GetProperty("grant").GetProperty("gems").GetInt32());
			Assert.Equal(3, body.GetProperty("quantity").GetInt32());
			Assert.True(body.GetProperty("isTest").GetBoolean());

			var purchase = Assert.Single(server.Grantor.Grants).Purchase;
			Assert.Equal("GPA.1234-5678-9012-34567", purchase.OrderId);
			Assert.Equal("acc-hash", purchase.AccountToken);
			Assert.Equal(new DateTimeOffset(2026, 10, 15, 1, 2, 3, TimeSpan.Zero).AddTicks(1234567), purchase.PurchasedAt);

			Assert.Equal(1, _google.Count("purchases/products/gem_100/tokens/token-1:acknowledge"));
			Assert.All(_google.Requests, request => Assert.Equal("Bearer " + FakeGooglePlay.AccessToken, request.Authorization));
		}

		[Fact]
		public async Task Acknowledged_purchase_and_disabled_acknowledge_skip_the_call()
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100", ack: "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED"));
			_google.Products["token-2"] = (HttpStatusCode.OK, FakeGooglePlay.Product("no_ads"));

			await using (var server = await StartAsync())
			{
				await server.RedeemAsync(Request("gem_100", "token-1"));
			}

			await using (var server = await StartAsync(options => options.AcknowledgeAfterGrant = false))
			{
				var (_, body) = await server.RedeemAsync(Request("no_ads", "token-2"));
				Assert.Equal(IapRedeemStatus.Granted, body.Status());
			}

			Assert.Equal(0, _google.Count(":acknowledge"));
		}

		[Fact]
		public async Task Acknowledged_grant_is_not_left_pending()
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100"));
			_google.Products["token-2"] = (HttpStatusCode.OK, FakeGooglePlay.Product("no_ads", ack: "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED"));
			await using var server = await StartAsync();

			await server.RedeemAsync(Request("gem_100", "token-1"));
			await server.RedeemAsync(Request("no_ads", "token-2"));

			Assert.False(await PendingAsync(server, "token-1"));
			Assert.False(await PendingAsync(server, "token-2"));
		}

		[Fact]
		public async Task Acknowledge_failure_still_grants_and_the_worker_retries_until_it_succeeds()
		{
			// 확인 직후 실패(또는 그 사이 서버 종료)해도 원장의 대기 표시로 다시 확인한다. 클라이언트가 돌아오지 않아도 3일 자동 환불을 막기 위해서다
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100"));
			_google.AcknowledgeStatus = HttpStatusCode.ServiceUnavailable;
			await using var server = await StartAsync();

			var (_, body) = await server.RedeemAsync(Request("gem_100", "token-1"));

			Assert.Equal(IapRedeemStatus.Granted, body.Status());
			Assert.True(await PendingAsync(server, "token-1"));
			Assert.Equal(0, await Worker(server).RunOnceAsync(CancellationToken.None));
			Assert.True(await PendingAsync(server, "token-1"));

			// 실패한 기록은 간격 뒤로 미뤄져 그 전에는 다시 묻지 않는다
			_google.AcknowledgeStatus = HttpStatusCode.OK;
			Assert.Equal(0, await Worker(server).RunOnceAsync(CancellationToken.None));
			Assert.Equal(2, _google.Count(":acknowledge"));

			server.Time.Advance(RetryInterval);
			Assert.Equal(1, await Worker(server).RunOnceAsync(CancellationToken.None));
			Assert.False(await PendingAsync(server, "token-1"));
			Assert.Equal(3, _google.Count(":acknowledge"));
			Assert.Equal(1, server.Grantor.Calls);
		}

		[Fact]
		public async Task Refused_acknowledge_checks_whether_the_purchase_is_already_settled()
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100"));
			_google.AcknowledgeStatus = HttpStatusCode.BadRequest;
			await using var server = await StartAsync();
			await server.RedeemAsync(Request("gem_100", "token-1"));
			Assert.True(await PendingAsync(server, "token-1"));

			// 클라이언트가 확정(소비)해 이미 확인된 결제
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100", ack: "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED"));

			Assert.Equal(1, await Worker(server).RunOnceAsync(CancellationToken.None));
			Assert.False(await PendingAsync(server, "token-1"));
		}

		[Fact]
		public async Task Failing_records_do_not_block_later_ones()
		{
			// 확인 기한이 다가온 뒤의 기록이 앞에서 계속 실패하는 기록 뒤에 막히지 않아야 한다(리뷰 2회차)
			await using var server = await StartAsync();
			var count = PendingAcknowledgementWorker.BatchSize + 1;

			for (var i = 0; i < count; i++)
			{
				var token = "token-" + i;

				if (i < count - 1)
				{
					_google.AcknowledgeStatuses[token] = HttpStatusCode.ServiceUnavailable;
				}

				var record = new PurchaseRecord
				{
					Store = IapStoreNames.GooglePlay, TransactionId = token, AccountId = "account-1", ProductId = "gem_100", StoreProductId = "gem_100",
					PurchasedAt = IapTestServer.Now, RedeemedAt = IapTestServer.Now, AcknowledgementDueAt = IapTestServer.Now.AddMinutes(-count + i)
				};
				await server.Ledger.RedeemAsync(record, _ => Task.FromResult(LedgerGrant.Granted(null)), CancellationToken.None);
			}

			Assert.Equal(1, await Worker(server).RunOnceAsync(CancellationToken.None));
			Assert.False(await PendingAsync(server, "token-" + (count - 1)));
			Assert.True(await PendingAsync(server, "token-0"));
			Assert.Equal(count, _google.Count(":acknowledge"));
		}

		[Theory]
		[InlineData("")]
		[InlineData("ACKNOWLEDGEMENT_STATE_SOMETHING_NEW")]
		public async Task Unknown_acknowledgement_state_is_left_for_acknowledgement(string state)
		{
			var json = FakeGooglePlay.Product("gem_100").Replace("\"acknowledgementState\":\"ACKNOWLEDGEMENT_STATE_PENDING\"", "\"acknowledgementState\":\"" + state + "\"");
			_google.Products["token-1"] = (HttpStatusCode.OK, json);
			_google.AcknowledgeStatus = HttpStatusCode.ServiceUnavailable;
			await using var server = await StartAsync();

			var (_, body) = await server.RedeemAsync(Request("gem_100", "token-1"));

			Assert.Equal(IapRedeemStatus.Granted, body.Status());
			Assert.Equal(1, _google.Count(":acknowledge"));
			Assert.True(await PendingAsync(server, "token-1"));
		}

		[Fact]
		public async Task Acknowledge_gives_up_after_the_three_day_window()
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100"));
			_google.AcknowledgeStatus = HttpStatusCode.ServiceUnavailable;
			await using var server = await StartAsync();
			await server.RedeemAsync(Request("gem_100", "token-1"));
			var calls = _google.Count(":acknowledge");

			server.Time.Advance(TimeSpan.FromDays(4));

			Assert.Equal(1, await Worker(server).RunOnceAsync(CancellationToken.None));
			Assert.False(await PendingAsync(server, "token-1"));
			Assert.Equal(calls, _google.Count(":acknowledge"));
		}

		[Fact]
		public async Task Worker_runs_at_startup_and_survives_ledger_failures()
		{
			await using var server = await IapTestServer.StartAsync(iap => iap.AddGooglePlay(options => options.PackageName = FakeGooglePlay.Package)
				.AddLedger<FlakyLedger>(ServiceLifetime.Singleton), services: services =>
			{
				services.AddSingleton<IGooglePlayAccessTokenSource>(_tokens);
				services.AddHttpClient(GooglePlayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _google);
			});
			var ledger = (FlakyLedger) server.Services.GetRequiredService<IPurchaseLedger>();

			await WaitAsync(() => ledger.PendingReads >= 1);
			server.Time.Advance(TimeSpan.FromMinutes(5));
			await WaitAsync(() => ledger.PendingReads >= 2);
		}

		private static async Task WaitAsync(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow.AddSeconds(5);

			while (!condition())
			{
				Assert.True(DateTime.UtcNow < deadline, "timed out");
				await Task.Delay(20);
			}
		}

		/// <summary>첫 대기 기록 조회에서 예외를 던지는 원장입니다(DB 장애).</summary>
		private sealed class FlakyLedger : IPurchaseLedger
		{
			private readonly InMemoryPurchaseLedger _inner = new InMemoryPurchaseLedger();
			private int _pendingReads;

			public int PendingReads => Volatile.Read(ref _pendingReads);

			public Task<PurchaseRecord> FindAsync(string store, string transactionId, CancellationToken cancellationToken)
			{
				return _inner.FindAsync(store, transactionId, cancellationToken);
			}

			public Task<LedgerRedeemResult> RedeemAsync(PurchaseRecord record, Func<CancellationToken, Task<LedgerGrant>> grant, CancellationToken cancellationToken)
			{
				return _inner.RedeemAsync(record, grant, cancellationToken);
			}

			public Task<System.Collections.Generic.IReadOnlyList<PurchaseRecord>> FindDueAcknowledgementsAsync(DateTimeOffset now, int limit,
				CancellationToken cancellationToken)
			{
				return Interlocked.Increment(ref _pendingReads) == 1
					? Task.FromException<System.Collections.Generic.IReadOnlyList<PurchaseRecord>>(new InvalidOperationException("database is down"))
					: _inner.FindDueAcknowledgementsAsync(now, limit, cancellationToken);
			}

			public Task MarkAcknowledgedAsync(string store, string transactionId, CancellationToken cancellationToken)
			{
				return _inner.MarkAcknowledgedAsync(store, transactionId, cancellationToken);
			}

			public Task DeferAcknowledgementAsync(string store, string transactionId, DateTimeOffset dueAt, CancellationToken cancellationToken)
			{
				return _inner.DeferAcknowledgementAsync(store, transactionId, dueAt, cancellationToken);
			}
		}

		[Fact]
		public async Task Duplicate_does_not_call_google()
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100"));
			await using var server = await StartAsync();
			await server.RedeemAsync(Request("gem_100", "token-1"));
			var calls = _google.Requests.Count;

			var (_, body) = await server.RedeemAsync(Request("gem_100", "token-1"));

			Assert.Equal(IapRedeemCodes.Duplicate, body.Code());
			Assert.Equal(calls, _google.Requests.Count);
		}

		[Theory]
		[InlineData("PENDING", IapRedeemStatus.RetryLater, IapRedeemCodes.PaymentPending)]
		[InlineData("CANCELLED", IapRedeemStatus.AlreadyGranted, IapRedeemCodes.Revoked)]
		[InlineData("PURCHASE_STATE_UNSPECIFIED", IapRedeemStatus.RetryLater, IapRedeemCodes.StoreUnavailable)]
		public async Task Purchase_state_decides_the_outcome(string state, IapRedeemStatus status, int code)
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100", state));
			await using var server = await StartAsync();

			var (_, body) = await server.RedeemAsync(Request("gem_100", "token-1"));

			Assert.Equal(status, body.Status());
			Assert.Equal(code, body.Code());
			Assert.Equal(0, server.Grantor.Calls);
			Assert.Null(await server.Ledger.FindAsync(IapStoreNames.GooglePlay, "token-1", CancellationToken.None));
		}

		[Theory]
		[InlineData(HttpStatusCode.NotFound, IapRedeemStatus.Rejected, IapRedeemCodes.InvalidReceipt)]
		[InlineData(HttpStatusCode.BadRequest, IapRedeemStatus.Rejected, IapRedeemCodes.InvalidReceipt)]
		[InlineData(HttpStatusCode.Gone, IapRedeemStatus.Rejected, IapRedeemCodes.InvalidReceipt)]
		[InlineData(HttpStatusCode.Unauthorized, IapRedeemStatus.RetryLater, IapRedeemCodes.StoreUnavailable)]
		[InlineData(HttpStatusCode.Forbidden, IapRedeemStatus.RetryLater, IapRedeemCodes.StoreUnavailable)]
		[InlineData(HttpStatusCode.TooManyRequests, IapRedeemStatus.RetryLater, IapRedeemCodes.StoreUnavailable)]
		[InlineData(HttpStatusCode.InternalServerError, IapRedeemStatus.RetryLater, IapRedeemCodes.StoreUnavailable)]
		public async Task Http_status_decides_the_outcome(HttpStatusCode http, IapRedeemStatus status, int code)
		{
			_google.Products["token-1"] = (http, "{\"error\":{}}");
			await using var server = await StartAsync();

			var (_, body) = await server.RedeemAsync(Request("gem_100", "token-1"));

			Assert.Equal(status, body.Status());
			Assert.Equal(code, body.Code());
		}

		[Fact]
		public async Task Unreadable_body_network_failure_timeout_and_refused_token_retry_later()
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, "{not json");
			await using var server = await StartAsync();

			var (_, unreadable) = await server.RedeemAsync(Request("gem_100", "token-1"));
			_google.Throw = new HttpRequestException("connection refused");
			var (_, network) = await server.RedeemAsync(Request("gem_100", "token-1"));
			_google.Throw = null;
			_google.Delay = TimeSpan.FromSeconds(5);
			var (_, timeout) = await server.RedeemAsync(Request("gem_100", "token-1"));
			_google.Delay = null;
			_tokens.Throw = new TokenResponseException(new TokenErrorResponse { Error = "invalid_grant" });
			var (_, token) = await server.RedeemAsync(Request("gem_100", "token-1"));

			Assert.All(new[] { unreadable, network, timeout, token }, body =>
			{
				Assert.Equal(IapRedeemStatus.RetryLater, body.Status());
				Assert.Equal(IapRedeemCodes.StoreUnavailable, body.Code());
			});
			Assert.Equal(0, server.Grantor.Calls);
		}

		[Theory]
		[InlineData("{}")]
		[InlineData("{\"productLineItem\":[],\"purchaseStateContext\":{\"purchaseState\":\"PURCHASED\"}}")]
		[InlineData("{\"productLineItem\":[{}],\"purchaseStateContext\":{\"purchaseState\":\"PURCHASED\"}}")]
		public async Task Product_response_without_line_items_retries_later(string json)
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, json);
			_google.Subscriptions["sub-1"] = (HttpStatusCode.OK, json.Replace("productLineItem", "lineItems"));
			await using var server = await StartAsync();

			var (_, product) = await server.RedeemAsync(Request("gem_100", "token-1"));
			var (_, subscription) = await server.RedeemAsync(Request("vip_month", "sub-1"));

			Assert.Equal(IapRedeemCodes.StoreUnavailable, product.Code());
			Assert.Equal(IapRedeemCodes.StoreUnavailable, subscription.Code());
		}

		[Fact]
		public async Task Other_product_in_the_purchase_is_a_mismatch()
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_500"));
			await using var server = await StartAsync();

			var (_, body) = await server.RedeemAsync(Request("gem_100", "token-1"));

			Assert.Equal(IapRedeemStatus.Rejected, body.Status());
			Assert.Equal(IapRedeemCodes.ProductMismatch, body.Code());
		}

		[Fact]
		public async Task Active_subscription_is_granted_with_expiry_and_acknowledged()
		{
			_google.Subscriptions["sub-1"] = (HttpStatusCode.OK, FakeGooglePlay.Subscription("vip_month"));
			await using var server = await StartAsync();

			var (_, body) = await server.RedeemAsync(Request("vip_month", "sub-1"));

			Assert.Equal(IapRedeemStatus.Granted, body.Status());
			Assert.Equal(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero), body.GetProperty("expiresAt").GetDateTimeOffset());
			var purchase = Assert.Single(server.Grantor.Grants).Purchase;
			Assert.Equal(IapSubscriptionState.Active, purchase.SubscriptionState);
			Assert.Equal("GPA.1111-2222-3333-44444..0", purchase.OrderId);
			Assert.Equal("acc-hash", purchase.AccountToken);
			Assert.Equal(1, _google.Count("purchases/subscriptions/vip_month/tokens/sub-1:acknowledge"));
			Assert.Equal(0, _google.Count("productsv2"));
		}

		[Theory]
		[InlineData("SUBSCRIPTION_STATE_PENDING", IapRedeemStatus.RetryLater, IapRedeemCodes.PaymentPending)]
		[InlineData("SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED", IapRedeemStatus.AlreadyGranted, IapRedeemCodes.Revoked)]
		[InlineData("SUBSCRIPTION_STATE_SOMETHING_NEW", IapRedeemStatus.RetryLater, IapRedeemCodes.StoreUnavailable)]
		[InlineData("SUBSCRIPTION_STATE_EXPIRED", IapRedeemStatus.Granted, IapRedeemCodes.None)]
		public async Task Subscription_state_decides_the_outcome(string state, IapRedeemStatus status, int code)
		{
			_google.Subscriptions["sub-1"] = (HttpStatusCode.OK, FakeGooglePlay.Subscription("vip_month", state));
			await using var server = await StartAsync();

			var (_, body) = await server.RedeemAsync(Request("vip_month", "sub-1"));

			Assert.Equal(status, body.Status());
			Assert.Equal(code, body.Code());
		}

		[Fact]
		public async Task Order_total_is_read_when_enabled()
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100", orderId: "GPA.1"));
			_google.Products["token-2"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100", orderId: "GPA.2"));
			_google.Orders["GPA.1"] = (HttpStatusCode.OK, "{\"orderId\":\"GPA.1\",\"total\":{\"currencyCode\":\"USD\",\"units\":\"1\",\"nanos\":990000000}}");
			_google.Orders["GPA.2"] = (HttpStatusCode.Forbidden, "{}");
			await using var server = await StartAsync(options => options.ReadOrderTotal = true);

			var (_, paid) = await server.RedeemAsync(Request("gem_100", "token-1"));
			var (_, unknown) = await server.RedeemAsync(Request("gem_100", "token-2"));

			Assert.Equal(1.99m, paid.GetProperty("paidAmount").GetDecimal());
			Assert.Equal("USD", paid.GetProperty("paidCurrency").GetString());
			Assert.Equal(IapRedeemStatus.Granted, unknown.Status());
			Assert.False(unknown.Has("paidAmount"));
		}

		[Fact]
		public void Missing_package_name_fails_at_startup()
		{
			var app = IapTestServer.Build(TimeProvider.System, iap => iap.AddGooglePlay(_ => { }), null,
				services => services.AddSingleton<IGooglePlayAccessTokenSource>(_tokens), "Development");

			Assert.ThrowsAny<Microsoft.Extensions.Options.OptionsValidationException>(() => app.MapGeunedaIap("/iap", IapTestServer.ResolveAccount));
		}

		[Theory]
		[InlineData("2026-10-15T01:02:03Z", 0)]
		[InlineData("2026-10-15T01:02:03.5Z", 5_000_000)]
		[InlineData("2026-10-15T01:02:03.123456789Z", 1_234_567)]
		[InlineData("2026-10-15T10:02:03.1+09:00", 1_000_000)]
		public void Rfc3339_times_keep_up_to_seven_fraction_digits(string text, long ticks)
		{
			Assert.Equal(new DateTimeOffset(2026, 10, 15, 1, 2, 3, TimeSpan.Zero).AddTicks(ticks), GooglePlayVerifier.ParseTime(text));
		}
	}
}
