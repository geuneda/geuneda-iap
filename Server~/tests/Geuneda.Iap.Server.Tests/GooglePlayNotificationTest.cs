using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>Google Play 실시간 개발자 알림(Pub/Sub 푸시)을 테스트 OIDC 발급자와 가짜 Android Publisher API 로 확인합니다.</summary>
	public sealed class GooglePlayNotificationTest : IAsyncLifetime
	{
		private const string Route = "notifications/google-play";
		private static readonly DateTimeOffset Event = new DateTimeOffset(2026, 10, 15, 1, 0, 0, TimeSpan.Zero);

		private readonly FakeGooglePlay _google = new FakeGooglePlay();
		private TestPubSubIssuer _issuer;
		private IapTestServer _server;

		public async Task InitializeAsync()
		{
			_issuer = await TestPubSubIssuer.StartAsync();
			_server = await IapTestServer.StartAsync(iap => iap.AddGooglePlay(options =>
			{
				options.PackageName = FakeGooglePlay.Package;
				options.NotificationAudience = TestPubSubIssuer.Audience;
				options.NotificationServiceAccountEmail = TestPubSubIssuer.ServiceAccount;
				options.NotificationCertificatesUrl = _issuer.CertificatesUrl;
			}).AddNotificationHandler<TestNotificationHandler>(ServiceLifetime.Singleton), services: services =>
			{
				services.AddSingleton<IGooglePlayAccessTokenSource>(new FakeTokenSource());
				services.AddHttpClient(GooglePlayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _google);
			}, map: app => app.MapGeunedaIapGooglePlayNotifications("/iap"));
		}

		public async Task DisposeAsync()
		{
			await _server.DisposeAsync();
			await _issuer.DisposeAsync();
		}

		private TestNotificationHandler Handler => _server.Notifications;

		private Task<int> PushAsync(object notification, string token = null)
		{
			return _server.PostAsync(Route, TestPubSubIssuer.Push(notification), token ?? _issuer.Token());
		}

		private static object Voided(string token, int refundType = 1, int productType = 2, string orderId = "GPA.1", DateTimeOffset? at = null)
		{
			return new
			{
				version = "1.0", packageName = FakeGooglePlay.Package, eventTimeMillis = (at ?? Event).ToUnixTimeMilliseconds().ToString(),
				voidedPurchaseNotification = new { purchaseToken = token, orderId, productType, refundType }
			};
		}

		private static object Subscription(string token, int type, DateTimeOffset? at = null)
		{
			return new
			{
				version = "1.0", packageName = FakeGooglePlay.Package, eventTimeMillis = (at ?? Event).ToUnixTimeMilliseconds(),
				subscriptionNotification = new { version = "1.0", notificationType = type, purchaseToken = token }
			};
		}

		[Fact]
		public async Task Push_tokens_must_be_google_signed_for_the_audience_and_push_account()
		{
			var notification = new { version = "1.0", packageName = FakeGooglePlay.Package, testNotification = new { version = "1.0" } };
			await using var stranger = await TestPubSubIssuer.StartAsync();

			Assert.Equal(401, await _server.PostAsync(Route, TestPubSubIssuer.Push(notification)));
			Assert.Equal(401, await PushAsync(notification, _issuer.Token(audience: "https://other.example.com")));
			Assert.Equal(401, await PushAsync(notification, _issuer.Token(email: "someone@test-project.iam.gserviceaccount.com")));
			Assert.Equal(401, await PushAsync(notification, _issuer.Token(emailVerified: false)));
			Assert.Equal(401, await PushAsync(notification, _issuer.Token(issuer: "https://evil.example.com")));
			Assert.Equal(401, await PushAsync(notification, _issuer.Token(age: TimeSpan.FromHours(2))));
			Assert.Equal(401, await PushAsync(notification, stranger.Token()));
			Assert.Equal(200, await PushAsync(notification));
		}

		[Fact]
		public async Task Full_refund_revokes_once_and_closes_a_redelivered_transaction()
		{
			await _server.GrantAsync(IapStoreNames.GooglePlay, "token-1", "gem_100", account: "account-7");

			Assert.Equal(200, await PushAsync(Voided("token-1")));
			Assert.Equal(200, await PushAsync(Voided("token-1")));

			var refund = Assert.Single(Handler.Refunds);
			Assert.Equal("account-7", refund.AccountId);
			Assert.Equal(1, refund.Quantity);
			Assert.True(refund.IsFullRefund);
			Assert.Equal(Event, refund.ObservedAt);
			Assert.Equal("gem_100", refund.Product.Id);

			var (_, body) = await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "gem_100", "token-1"), "account-7");
			Assert.Equal(IapRedeemStatus.AlreadyGranted, body.Status());
			Assert.Equal(IapRedeemCodes.Revoked, body.Code());
		}

		[Fact]
		public async Task Partial_refunds_revoke_the_newly_refunded_quantity()
		{
			await _server.GrantAsync(IapStoreNames.GooglePlay, "token-1", "gem_100", quantity: 3);
			_google.Products["token-1"] = (HttpStatusCode.OK, Refundable(2));

			Assert.Equal(200, await PushAsync(Voided("token-1", refundType: 2, at: Event)));
			_google.Products["token-1"] = (HttpStatusCode.OK, Refundable(0));
			Assert.Equal(200, await PushAsync(Voided("token-1", refundType: 2, at: Event.AddMinutes(2))));
			// 늦게 끝난 앞 조회가 더 많은 환불 가능 수량을 돌려줘도 되돌려 주지 않는다(Google 은 환불을 취소하지 않음, 리뷰 2b 1회차)
			_google.Products["token-1"] = (HttpStatusCode.OK, Refundable(2));
			Assert.Equal(200, await PushAsync(Voided("token-1", refundType: 2, at: Event.AddMinutes(4))));
			Assert.Equal(200, await PushAsync(Voided("token-1", refundType: 1, at: Event.AddMinutes(3))));

			Assert.Equal(new[] { 1, 2 }, Handler.Refunds.Select(refund => refund.Quantity));
			Assert.Equal(new[] { 1, 3 }, Handler.Refunds.Select(refund => refund.RefundedQuantity));
			Assert.Empty(Handler.Reversals);
		}

		[Theory]
		[InlineData(2, 3)]
		[InlineData(2, 0)]
		[InlineData(9, 1)]
		[InlineData(0, 1)]
		public async Task Unknown_voided_types_are_retried_without_revoking(int productType, int refundType)
		{
			await _server.GrantAsync(IapStoreNames.GooglePlay, "token-1", "gem_100");

			Assert.Equal(503, await PushAsync(Voided("token-1", refundType: refundType, productType: productType)));
			Assert.Empty(Handler.Refunds);
		}

		[Fact]
		public async Task Refund_during_an_uncommitted_grant_waits_for_the_commit_and_revokes()
		{
			// 지급 트랜잭션이 커밋되기 전에 온 환불을 성공으로 버리면 회수할 길이 없다(리뷰 2b 1·2회차)
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100"));
			var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			_server.Grantor.Enqueue(async (grant, token) =>
			{
				entered.SetResult(true);
				await release.Task;
				return GrantResult.Granted(new TestReward { Gems = 100 });
			});

			var redeem = _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "gem_100", "token-1"));
			await entered.Task;
			// 환불은 같은 거래 잠금에서 지급 커밋을 기다렸다가 지급 기록을 회수한다
			var push = PushAsync(Voided("token-1"));
			await Task.Delay(200);
			Assert.False(push.IsCompleted);
			release.SetResult(true);

			Assert.Equal(IapRedeemStatus.Granted, (await redeem).Body.Status());
			Assert.Equal(200, await push);
			Assert.Equal(1, Assert.Single(Handler.Refunds).Quantity);
		}

		[Fact]
		public async Task Partial_refund_without_a_readable_purchase_is_retried()
		{
			await _server.GrantAsync(IapStoreNames.GooglePlay, "token-1", "gem_100", quantity: 3);
			_google.Products["token-1"] = (HttpStatusCode.ServiceUnavailable, "{}");

			Assert.Equal(503, await PushAsync(Voided("token-1", refundType: 2)));
			Assert.Empty(Handler.Refunds);
		}

		[Fact]
		public async Task Revoked_subscription_before_the_grant_closes_the_later_redeem()
		{
			_google.Subscriptions["sub-1"] = (HttpStatusCode.OK, FakeGooglePlay.Subscription("vip_month", "SUBSCRIPTION_STATE_EXPIRED"));

			Assert.Equal(200, await PushAsync(Subscription("sub-1", 12)));
			var (_, body) = await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "vip_month", "sub-1"));

			Assert.Equal(IapRedeemCodes.Revoked, body.Code());
			Assert.Equal(0, _server.Grantor.Calls);
		}

		[Fact]
		public async Task Refund_before_the_grant_closes_the_later_redeem()
		{
			// 지급 요청 전에 전부 환불됐으면 나중에 그 토큰을 내도 지급하지 않는다(리뷰 2b 2회차)
			_google.Products["token-1"] = (HttpStatusCode.OK, FakeGooglePlay.Product("gem_100"));

			Assert.Equal(200, await PushAsync(Voided("token-1")));
			var (_, body) = await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "gem_100", "token-1"));

			Assert.Equal(IapRedeemStatus.AlreadyGranted, body.Status());
			Assert.Equal(IapRedeemCodes.Revoked, body.Code());
			Assert.Equal(0, _server.Grantor.Calls);
			Assert.Empty(Handler.Refunds);
		}

		[Fact]
		public async Task Partial_refund_before_the_grant_grants_the_rest()
		{
			_google.Products["token-1"] = (HttpStatusCode.OK, Refundable(1));

			Assert.Equal(200, await PushAsync(Voided("token-1", refundType: 2)));
			var (_, body) = await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "gem_100", "token-1"));
			Assert.Equal(200, await PushAsync(Voided("token-1", refundType: 1, at: Event.AddMinutes(1))));

			Assert.Equal(IapRedeemStatus.Granted, body.Status());
			Assert.Equal(1, Assert.Single(_server.Grantor.Grants).Quantity);
			var refund = Assert.Single(Handler.Refunds);
			Assert.Equal(1, refund.Quantity);
			Assert.True(refund.IsFullRefund);
		}

		[Fact]
		public async Task Unknown_purchase_that_cannot_be_read_is_retried()
		{
			Assert.Equal(503, await PushAsync(Voided("unknown-token")));
			Assert.Empty(Handler.Refunds);
		}

		[Fact]
		public async Task Concurrent_subscription_reads_cannot_bring_back_an_older_state()
		{
			// 먼저 시작한 조회가 늦게 끝나도(옛 상태) 새 상태를 덮지 않는다. 조회와 전달을 원장의 구독 잠금 안에서 하나씩 한다(리뷰 2b 2회차)
			await _server.GrantAsync(IapStoreNames.GooglePlay, "sub-1", "vip_month", account: "account-7");
			var script = new System.Collections.Concurrent.ConcurrentQueue<(TimeSpan Delay, string Json)>();
			script.Enqueue((TimeSpan.FromMilliseconds(400), FakeGooglePlay.Subscription("vip_month")));
			script.Enqueue((TimeSpan.Zero, FakeGooglePlay.Subscription("vip_month", "SUBSCRIPTION_STATE_EXPIRED")));
			_google.SubscriptionScripts["sub-1"] = script;

			var results = await Task.WhenAll(PushAsync(Subscription("sub-1", 2)), PushAsync(Subscription("sub-1", 12)));

			Assert.All(results, status => Assert.Equal(200, status));
			Assert.Equal(new[] { IapSubscriptionState.Active, IapSubscriptionState.Expired }, Handler.Subscriptions.Select(update => update.State));
			Assert.True(Handler.Subscriptions.Last().ObservedAt > Handler.Subscriptions.First().ObservedAt);
		}

		[Fact]
		public async Task Handler_failure_is_retried_and_applied_once()
		{
			await _server.GrantAsync(IapStoreNames.GooglePlay, "token-1", "gem_100");
			Handler.ThrowOnce(new InvalidOperationException("database is down"));

			Assert.Equal(500, await PushAsync(Voided("token-1")));
			Assert.Equal(0, (await _server.Ledger.FindAsync(IapStoreNames.GooglePlay, "token-1", CancellationToken.None)).RefundedQuantity);
			Assert.Equal(200, await PushAsync(Voided("token-1")));
			Assert.Equal(200, await PushAsync(Voided("token-1")));

			Assert.Single(Handler.Refunds);
		}

		[Fact]
		public async Task Subscription_notification_delivers_the_current_state_to_the_owner()
		{
			await _server.GrantAsync(IapStoreNames.GooglePlay, "sub-1", "vip_month", account: "account-7");
			_google.Subscriptions["sub-1"] = (HttpStatusCode.OK, FakeGooglePlay.Subscription("vip_month", "SUBSCRIPTION_STATE_IN_GRACE_PERIOD"));

			Assert.Equal(200, await PushAsync(Subscription("sub-1", 6)));

			var update = Assert.Single(Handler.Subscriptions);
			Assert.Equal("account-7", update.AccountId);
			Assert.Equal(IapSubscriptionState.GracePeriod, update.State);
			Assert.Equal("SUBSCRIPTION_IN_GRACE_PERIOD", update.NotificationType);
			Assert.Equal(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero), update.ExpiresAt);
			Assert.True(update.AutoRenewing);
			Assert.Equal("vip_month", update.Product.Id);
			// 조회한 때의 상태라 알림 시각이 아니라 조회 시각이다
			Assert.Equal(IapTestServer.Now, update.ObservedAt);
			Assert.Equal("GPA.1111-2222-3333-44444..0", update.LatestTransactionId);
			Assert.Equal(0, _google.Count(":acknowledge"));
		}

		[Fact]
		public async Task Upgraded_subscription_uses_the_linked_owner_before_its_own_redeem()
		{
			await _server.GrantAsync(IapStoreNames.GooglePlay, "sub-old", "vip_month", account: "account-7");
			_google.Subscriptions["sub-new"] = (HttpStatusCode.OK,
				FakeGooglePlay.Subscription("vip_month").Replace("\"subscriptionState\"", "\"linkedPurchaseToken\":\"sub-old\",\"subscriptionState\""));

			Assert.Equal(200, await PushAsync(Subscription("sub-new", 4)));

			var update = Assert.Single(Handler.Subscriptions);
			Assert.Equal("account-7", update.AccountId);
			Assert.Equal("sub-old", update.LinkedSubscriptionId);
			Assert.Equal("sub-new", update.SubscriptionId);
		}

		[Fact]
		public async Task Subscription_lookup_failures_retry_or_ignore()
		{
			_google.Subscriptions["sub-down"] = (HttpStatusCode.InternalServerError, "{}");
			_google.Subscriptions["sub-pending"] = (HttpStatusCode.OK, FakeGooglePlay.Subscription("vip_month", "SUBSCRIPTION_STATE_PENDING"));
			_google.Subscriptions["sub-new-state"] = (HttpStatusCode.OK, FakeGooglePlay.Subscription("vip_month", "SUBSCRIPTION_STATE_SOMETHING_NEW"));
			_google.Subscriptions["sub-empty"] = (HttpStatusCode.OK, "{}");
			_google.Subscriptions["sub-no-items"] = (HttpStatusCode.OK, "{\"subscriptionState\":\"SUBSCRIPTION_STATE_ACTIVE\",\"lineItems\":[]}");

			Assert.Equal(503, await PushAsync(Subscription("sub-down", 2)));
			Assert.Equal(200, await PushAsync(Subscription("sub-gone", 13)));
			Assert.Equal(200, await PushAsync(Subscription("sub-pending", 4)));
			Assert.Equal(503, await PushAsync(Subscription("sub-new-state", 2)));
			Assert.Equal(503, await PushAsync(Subscription("sub-empty", 2)));
			Assert.Equal(503, await PushAsync(Subscription("sub-no-items", 2)));
			Assert.Empty(Handler.Subscriptions);
		}

		[Fact]
		public async Task Revoked_subscription_revokes_the_grant_and_reports_the_state()
		{
			await _server.GrantAsync(IapStoreNames.GooglePlay, "sub-1", "vip_month");
			_google.Subscriptions["sub-1"] = (HttpStatusCode.OK, FakeGooglePlay.Subscription("vip_month", "SUBSCRIPTION_STATE_EXPIRED"));

			Assert.Equal(200, await PushAsync(Subscription("sub-1", 12)));
			// 같은 철회의 환불 알림도 온다(원장이 한 번만 반영). 구독 조회는 잠금 안이라 두 번째도 더 늦은 관측으로 전달된다
			Assert.Equal(200, await PushAsync(Voided("sub-1", productType: 1, orderId: null)));

			Assert.Single(Handler.Refunds);
			Assert.Equal(new[] { IapSubscriptionState.Expired, IapSubscriptionState.Expired }, Handler.Subscriptions.Select(update => update.State));
		}

		[Fact]
		public async Task Subscription_refund_only_reports_the_state()
		{
			// 구독 결제 환불은 권한만 다시 맞추고 지급을 회수하지 않는다(철회는 SUBSCRIPTION_REVOKED 로 옴, 리뷰 2b 3회차).
			// 지급 전에 온 구독 환불도 원장에 기록하지 않아 나중의 지급을 막지 않는다(토큰 하나에 주문이 여럿)
			await _server.GrantAsync(IapStoreNames.GooglePlay, "sub-1", "vip_month", orderId: "GPA.9..0");
			_google.Subscriptions["sub-1"] = (HttpStatusCode.OK, FakeGooglePlay.Subscription("vip_month"));
			_google.Subscriptions["sub-2"] = (HttpStatusCode.OK, FakeGooglePlay.Subscription("vip_month"));

			Assert.Equal(200, await PushAsync(Voided("sub-1", productType: 1, orderId: "GPA.9..1")));
			Assert.Equal(200, await PushAsync(Voided("sub-1", productType: 1, orderId: "GPA.9..0")));
			Assert.Equal(200, await PushAsync(Voided("sub-2", productType: 1, orderId: "GPA.7..0")));
			var (_, body) = await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "vip_month", "sub-2"));

			Assert.Empty(Handler.Refunds);
			Assert.Equal(IapRedeemStatus.Granted, body.Status());

			// 알림 셋과 sub-2 지급의 구독 동기화(지급 트랜잭션 안, 그 계정 소유)
			Assert.Equal(4, Handler.Subscriptions.Count);
			var synced = Handler.Subscriptions.Last();
			Assert.Equal("sub-2", synced.SubscriptionId);
			Assert.Equal(IapTestServer.DefaultAccount, synced.AccountId);
			Assert.Equal(SubscriptionSync.Redeem, synced.NotificationType);
		}

		[Fact]
		public async Task Other_package_test_one_time_and_malformed_pushes()
		{
			await _server.GrantAsync(IapStoreNames.GooglePlay, "token-1", "gem_100");
			var otherPackage = new
			{
				version = "1.0", packageName = "com.other", eventTimeMillis = Event.ToUnixTimeMilliseconds(),
				voidedPurchaseNotification = new { purchaseToken = "token-1", orderId = "GPA.1", productType = 2, refundType = 1 }
			};
			var oneTime = new
			{
				version = "1.0", packageName = FakeGooglePlay.Package,
				oneTimeProductNotification = new { version = "1.0", notificationType = 1, purchaseToken = "token-9", sku = "gem_100" }
			};

			Assert.Equal(200, await PushAsync(otherPackage));
			Assert.Equal(200, await PushAsync(oneTime));
			Assert.Equal(400, await _server.PostAsync(Route, "{\"message\":{\"data\":\"not base64!\"}}", _issuer.Token()));
			Assert.Equal(400, await _server.PostAsync(Route, "{\"message\":{}}", _issuer.Token()));

			Assert.Empty(Handler.Refunds);
			Assert.Equal(0, _google.Count(":acknowledge"));
		}

		private static string Refundable(int quantity)
		{
			return FakeGooglePlay.Product("gem_100", quantity: 3).Replace("\"consumptionState\"", $"\"refundableQuantity\":{quantity},\"consumptionState\"");
		}
	}
}
