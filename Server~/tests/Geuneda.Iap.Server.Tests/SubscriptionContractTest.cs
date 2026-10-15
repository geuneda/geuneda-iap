using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>
	/// 서버가 합친 구독 권한 스냅숏(README '구독 권한')을 게임이 그대로 저장할 때(<see cref="SubscriptionBook"/>) 지급과 서버 알림이 겹치거나 순서가 바뀌어도 권한이 맞는지 확인합니다.
	/// 구독 지급은 지급 트랜잭션 안에서 구독 잠금을 잡고 합칩니다(Google 은 그 안에서 다시 읽음, App Store 는 거래의 기간으로). 리뷰 2b 4~7회차의 경계 사례입니다.
	/// </summary>
	public sealed class SubscriptionContractTest : IAsyncLifetime
	{
		private const long AppAppleId = 1234567890;
		private static readonly long SignedAt = IapTestServer.Now.AddHours(1).ToUnixTimeMilliseconds();

		private readonly FakeGooglePlay _google = new FakeGooglePlay();
		private readonly TestAppleSigner _signer = TestAppleSigner.Create();
		private readonly SubscriptionBook _book = new SubscriptionBook();
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
				}).AddAppStore(options =>
				{
					options.BundleId = TestAppleSigner.BundleId;
					options.OnlineChecks = false;
					options.AppAppleId = AppAppleId;
					options.RootCertificates.Add(_signer.RootDer);
				}).AddFakeStore().AddVerifier<PeriodlessVerifier>().AddGrantor<BookGrantor>(ServiceLifetime.Singleton).AddNotificationHandler<BookHandler>(ServiceLifetime.Singleton),
				services: services =>
				{
					services.AddSingleton(_book);
					services.AddSingleton<IGooglePlayAccessTokenSource>(new FakeTokenSource());
					services.AddHttpClient(GooglePlayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _google);
				},
				map: app =>
				{
					app.MapGeunedaIapGooglePlayNotifications("/iap");
					app.MapGeunedaIapAppStoreNotifications("/iap");
				});
		}

		public async Task DisposeAsync()
		{
			await _server.DisposeAsync();
			await _issuer.DisposeAsync();
			_signer.Dispose();
		}

		private static ConcurrentQueue<(TimeSpan Delay, string Json)> Reads(params string[] responses)
		{
			var script = new ConcurrentQueue<(TimeSpan Delay, string Json)>();

			foreach (var response in responses)
			{
				script.Enqueue((TimeSpan.Zero, response));
			}

			return script;
		}

		private Task<int> PushGoogleAsync(object notification)
		{
			return _server.PostAsync("notifications/google-play", TestPubSubIssuer.Push(notification), _issuer.Token());
		}

		private static object GoogleSubscription(string token, int type)
		{
			return new
			{
				version = "1.0", packageName = FakeGooglePlay.Package, eventTimeMillis = SignedAt,
				subscriptionNotification = new { version = "1.0", notificationType = type, purchaseToken = token }
			};
		}

		private static Dictionary<string, object> Renewal(int autoRenew = 1, long? graceUntil = null)
		{
			var renewal = new Dictionary<string, object>
			{
				{ "originalTransactionId", "1000000000000001" }, { "productId", "com.test.vip.month" }, { "autoRenewStatus", autoRenew },
				{ "isInBillingRetryPeriod", graceUntil != null }, { "signedDate", SignedAt }
			};

			if (graceUntil != null)
			{
				renewal["gracePeriodExpiresDate"] = graceUntil.Value;
			}

			return renewal;
		}

		private Task<(int Status, JsonElement Body)> RedeemAppleAsync(Dictionary<string, object> transaction)
		{
			return _server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "vip_month", (string) transaction["transactionId"], _signer.Sign(transaction)));
		}

		private SubscriptionUpdate AppleRow => _book.Find(IapStoreNames.AppleAppStore, "1000000000000001");

		[Fact]
		public async Task Older_redeem_read_keeps_the_newer_notification_state()
		{
			// 지급 검증이 계정 보류를 읽은 뒤 복구 알림이 이용 중을 저장한다. 지급 트랜잭션의 동기화는 잠금 안에서 다시 읽으므로 옛 조회를 쓰지 않는다(리뷰 2b 4회차)
			_google.SubscriptionScripts["sub-9"] = Reads(FakeGooglePlay.Subscription("vip_month", "SUBSCRIPTION_STATE_ON_HOLD"),
				FakeGooglePlay.Subscription("vip_month", expiry: "2026-12-15T00:00:00Z"), FakeGooglePlay.Subscription("vip_month", expiry: "2026-12-15T00:00:00Z"));
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			_book.HoldGrant = release.Task;

			var redeem = _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "vip_month", "sub-9"));
			await _book.GrantEntered.Task;
			Assert.Equal(200, await PushGoogleAsync(GoogleSubscription("sub-9", 1)));
			release.SetResult(true);

			Assert.Equal(IapRedeemStatus.Granted, (await redeem).Body.Status());
			var row = _book.Find(IapStoreNames.GooglePlay, "sub-9");
			Assert.Equal(IapSubscriptionState.Active, row.State);
			Assert.Equal(new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero), row.ExpiresAt);
			Assert.Equal(IapTestServer.DefaultAccount, row.AccountId);
		}

		[Fact]
		public async Task Stale_notification_state_is_replaced_by_the_grant_sync()
		{
			// 계정 보류 알림이 지급 전에 저장되고, 스토어에서 복구된 뒤 복구 알림보다 먼저 첫 지급이 온다. 동기화가 지금 상태로 올린다(리뷰 2b 7회차)
			_google.SubscriptionScripts["sub-6"] = Reads(FakeGooglePlay.Subscription("vip_month", "SUBSCRIPTION_STATE_ON_HOLD"), FakeGooglePlay.Subscription("vip_month"),
				FakeGooglePlay.Subscription("vip_month"));

			Assert.Equal(200, await PushGoogleAsync(GoogleSubscription("sub-6", 5)));
			Assert.Null(_book.Find(IapStoreNames.GooglePlay, "sub-6").AccountId);
			var (_, body) = await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "vip_month", "sub-6"));

			Assert.Equal(IapRedeemStatus.Granted, body.Status());
			var row = _book.Find(IapStoreNames.GooglePlay, "sub-6");
			Assert.Equal(IapSubscriptionState.Active, row.State);
			Assert.Equal(IapTestServer.DefaultAccount, row.AccountId);
			Assert.Equal(SubscriptionSync.Redeem, row.NotificationType);
		}

		[Fact]
		public async Task Subscription_refund_during_the_first_grant_is_not_undone()
		{
			// 구독 환불(voidedPurchase)은 거래 잠금 없이 상태만 넘긴다. 그 사이 지급 검증이 읽은 옛 상태가 그 상태를 되살리지 않는다(리뷰 2b 6회차)
			_google.SubscriptionScripts["sub-8"] = Reads(FakeGooglePlay.Subscription("vip_month"),
				FakeGooglePlay.Subscription("vip_month", "SUBSCRIPTION_STATE_EXPIRED", expiry: "2026-10-15T00:30:00Z"),
				FakeGooglePlay.Subscription("vip_month", "SUBSCRIPTION_STATE_EXPIRED", expiry: "2026-10-15T00:30:00Z"));
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			_book.HoldGrant = release.Task;

			var redeem = _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "vip_month", "sub-8"));
			await _book.GrantEntered.Task;
			var voided = new
			{
				version = "1.0", packageName = FakeGooglePlay.Package, eventTimeMillis = SignedAt.ToString(),
				voidedPurchaseNotification = new { purchaseToken = "sub-8", orderId = "GPA.1", productType = 1, refundType = 1 }
			};
			Assert.Equal(200, await PushGoogleAsync(voided));
			release.SetResult(true);

			Assert.Equal(IapRedeemStatus.Granted, (await redeem).Body.Status());
			var row = _book.Find(IapStoreNames.GooglePlay, "sub-8");
			Assert.Equal(IapSubscriptionState.Expired, row.State);
			Assert.Equal(new DateTimeOffset(2026, 10, 15, 0, 30, 0, TimeSpan.Zero), row.ExpiresAt);
			Assert.Equal(IapTestServer.DefaultAccount, row.AccountId);
		}

		[Fact]
		public async Task Revocation_during_the_grant_ends_expired()
		{
			// 철회의 환불은 지급 커밋을 기다리고, 상태는 그 뒤 잠금 안에서 다시 읽어 넘긴다
			var expired = FakeGooglePlay.Subscription("vip_month", "SUBSCRIPTION_STATE_EXPIRED", expiry: "2026-10-15T00:30:00Z");
			_google.SubscriptionScripts["sub-7"] = Reads(FakeGooglePlay.Subscription("vip_month"), expired, expired, expired);
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			_book.HoldGrant = release.Task;

			var redeem = _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "vip_month", "sub-7"));
			await _book.GrantEntered.Task;
			var push = PushGoogleAsync(GoogleSubscription("sub-7", 12));

			for (var wait = 0; wait < 100 && _google.Count("subscriptionsv2/tokens/sub-7") < 2; wait++)
			{
				await Task.Delay(50);
			}

			Assert.False(push.IsCompleted);
			release.SetResult(true);

			Assert.Equal(IapRedeemStatus.Granted, (await redeem).Body.Status());
			Assert.Equal(200, await push);
			var row = _book.Find(IapStoreNames.GooglePlay, "sub-7");
			Assert.Equal(IapSubscriptionState.Expired, row.State);
			Assert.Equal(new DateTimeOffset(2026, 10, 15, 0, 30, 0, TimeSpan.Zero), row.ExpiresAt);
			Assert.Equal(IapTestServer.DefaultAccount, row.AccountId);
		}

		[Fact]
		public async Task Unreadable_subscription_rolls_the_grant_back()
		{
			// 지급 트랜잭션 안에서 지금 상태를 읽지 못하면 지급까지 롤백하고 다시 받는다(지급만 남고 권한이 빠지지 않게)
			_google.SubscriptionScripts["sub-5"] = Reads(FakeGooglePlay.Subscription("vip_month"));
			_google.Subscriptions["sub-5"] = (System.Net.HttpStatusCode.ServiceUnavailable, "{}");

			var (_, deferred) = await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "vip_month", "sub-5"));
			Assert.Equal(IapRedeemStatus.RetryLater, deferred.Status());
			Assert.Equal(IapRedeemCodes.StoreUnavailable, deferred.Code());
			Assert.Null(await _server.Ledger.FindAsync(IapStoreNames.GooglePlay, "sub-5", System.Threading.CancellationToken.None));
			Assert.Null(_book.Find(IapStoreNames.GooglePlay, "sub-5"));

			_google.Subscriptions["sub-5"] = (System.Net.HttpStatusCode.OK, FakeGooglePlay.Subscription("vip_month"));
			var (_, granted) = await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.GooglePlay, "vip_month", "sub-5"));

			Assert.Equal(IapRedeemStatus.Granted, granted.Status());
			Assert.Equal(IapTestServer.DefaultAccount, _book.Find(IapStoreNames.GooglePlay, "sub-5").AccountId);
		}

		[Fact]
		public async Task Grace_from_a_notification_survives_a_later_signed_redeem_of_the_same_transaction()
		{
			// 지급 요청 전에 결제 유예 알림이 왔고, 같은 거래를 더 늦게 서명한 지급 요청(만료 시각만 아는 상태)이 온다. 계정만 연결된다(리뷰 2b 4회차)
			var expired = IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds();
			var grace = IapTestServer.Now.AddDays(3).ToUnixTimeMilliseconds();
			var transaction = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: expired);

			Assert.Equal(200, await _server.PostAsync("notifications/app-store", AppleNotification("DID_FAIL_TO_RENEW", transaction, Renewal(graceUntil: grace), 4)));
			var (_, body) = await RedeemAppleAsync(TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: expired, signedDate: SignedAt + 60_000));

			Assert.Equal(IapRedeemStatus.Granted, body.Status());
			Assert.Equal(IapSubscriptionState.GracePeriod, AppleRow.State);
			Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(grace), AppleRow.GracePeriodExpiresAt);
			Assert.True(AppleRow.AutoRenewing);
			Assert.Equal(IapTestServer.DefaultAccount, AppleRow.AccountId);
			Assert.Equal(SubscriptionSync.Redeem, AppleRow.NotificationType);
		}

		[Fact]
		public async Task Later_period_redeem_raises_the_state_without_its_notification()
		{
			// 알림 없이 지난 기간 거래(만료)와 뒤 기간의 갱신 거래가 차례로 지급된다. 갱신 알림이 늦거나 오지 않아도 권한이 오르고(리뷰 2b 5회차),
			// 늦게 처음 온 앞 기간 만료 알림과 그 재전달, 갱신 거래의 중복 요청은 되돌리지 않는다(리뷰 2b 6회차)
			var next = IapTestServer.Now.AddDays(29).ToUnixTimeMilliseconds();
			var first = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds());
			var renewal = TestAppleSigner.Transaction("2000000556", "com.test.vip.month", expiresDate: next,
				purchaseDate: IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds());

			await RedeemAppleAsync(first);
			Assert.Equal(IapSubscriptionState.Expired, AppleRow.State);
			Assert.Equal(IapRedeemStatus.Granted, (await RedeemAppleAsync(renewal)).Body.Status());
			Assert.Equal(IapSubscriptionState.Active, AppleRow.State);
			Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(next), AppleRow.ExpiresAt);

			var expiredNotification = AppleNotification("EXPIRED", first, Renewal(autoRenew: 0), 2, SignedAt - 600_000);
			Assert.Equal(200, await _server.PostAsync("notifications/app-store", expiredNotification));
			Assert.Equal(200, await _server.PostAsync("notifications/app-store", expiredNotification));
			Assert.Equal(IapRedeemCodes.Duplicate, (await RedeemAppleAsync(renewal)).Body.Code());
			Assert.Equal(IapSubscriptionState.Active, AppleRow.State);
			Assert.Equal(2, _book.Deliveries.Count);

			// 늦게 온 그 갱신 알림은 같은 기간이라 반영된다(자동 갱신 정보가 생김)
			Assert.Equal(200, await _server.PostAsync("notifications/app-store", AppleNotification("DID_RENEW", renewal, Renewal(), 1)));
			Assert.Equal(IapSubscriptionState.Active, AppleRow.State);
			Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(next), AppleRow.ExpiresAt);
			Assert.True(AppleRow.AutoRenewing);
			Assert.Equal(IapTestServer.DefaultAccount, AppleRow.AccountId);
		}

		[Fact]
		public async Task Ignored_earlier_period_notification_does_not_hold_back_a_later_one()
		{
			// 갱신 거래를 지급한 뒤, 앞 기간 거래의 환불 알림(늦게 서명)이 먼저 오고 현재 기간의 결제 유예 알림(더 일찍 서명)이 늦게 온다.
			// 버린 앞 기간 알림이 순서 기준을 올리지 않아 결제 유예가 반영된다(리뷰 2b 7회차)
			var expired = IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds();
			var grace = IapTestServer.Now.AddDays(3).ToUnixTimeMilliseconds();
			var renewal = TestAppleSigner.Transaction("2000000556", "com.test.vip.month", expiresDate: expired,
				purchaseDate: IapTestServer.Now.AddDays(-31).ToUnixTimeMilliseconds());
			var first = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(-31).ToUnixTimeMilliseconds(),
				revocationDate: SignedAt + 100_000);

			await RedeemAppleAsync(renewal);
			Assert.Equal(200, await _server.PostAsync("notifications/app-store", AppleNotification("REFUND", first, Renewal(), 1, SignedAt + 120_000)));
			Assert.Equal(200, await _server.PostAsync("notifications/app-store",
				AppleNotification("DID_FAIL_TO_RENEW", renewal, Renewal(graceUntil: grace), 4, SignedAt + 60_000)));

			Assert.Equal(IapSubscriptionState.GracePeriod, AppleRow.State);
			Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(grace), AppleRow.GracePeriodExpiresAt);
			Assert.Equal("2000000556", AppleRow.LatestTransactionId);
		}

		[Fact]
		public async Task Upgrade_to_a_shorter_product_takes_over_by_purchase_time()
		{
			// 긴 기간 상품에서 짧은 기간 상품으로 업그레이드하면 새 거래의 만료가 더 이르다. 기간은 거래의 구매 시각으로 정하고, 업그레이드로 철회된 이전 거래의 알림은 되돌리지 않는다(리뷰 2b 7회차)
			var yearly = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(300).ToUnixTimeMilliseconds());
			var upgraded = IapTestServer.Now.AddDays(29).ToUnixTimeMilliseconds();
			var monthly = TestAppleSigner.Transaction("2000000556", "com.test.vip.month", expiresDate: upgraded,
				purchaseDate: IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds());
			var revokedYearly = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(300).ToUnixTimeMilliseconds(),
				revocationDate: SignedAt);

			await RedeemAppleAsync(yearly);
			await RedeemAppleAsync(monthly);
			Assert.Equal(200, await _server.PostAsync("notifications/app-store", AppleNotification("REVOKE", revokedYearly, Renewal(), 1, SignedAt + 60_000)));

			Assert.Equal(IapSubscriptionState.Active, AppleRow.State);
			Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(upgraded), AppleRow.ExpiresAt);
			Assert.Equal("2000000556", AppleRow.LatestTransactionId);
		}

		[Fact]
		public async Task Owner_follows_the_subscription_lock_not_the_redeem_start()
		{
			// 계정 A 가 뒤 기간 거래 T3 를 먼저 요청해 지급자에서 기다리는 동안, 나중에 시작한 계정 B 의 앞 기간 거래 T2 지급이 먼저 끝난다. 구독 잠금을 나중에 통과한 A 의
			// 뒤 기간이 소유자이고, 그 뒤 알림이 지급 시작 시각이 늦은 B 로 되돌리지 않는다(리뷰 2b 8회차)
			var later = TestAppleSigner.Transaction("2000000557", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(29).ToUnixTimeMilliseconds(),
				purchaseDate: IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds());
			var earlier = TestAppleSigner.Transaction("2000000556", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds(),
				purchaseDate: IapTestServer.Now.AddDays(-31).ToUnixTimeMilliseconds());
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			_book.HoldGrant = release.Task;

			var first = _server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "vip_month", "2000000557", _signer.Sign(later)), "account-a");
			await _book.GrantEntered.Task;
			_server.Time.Advance(TimeSpan.FromMinutes(1));
			Assert.Equal(IapRedeemStatus.Granted, (await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "vip_month", "2000000556",
				_signer.Sign(earlier)), "account-b")).Body.Status());
			release.SetResult(true);
			Assert.Equal(IapRedeemStatus.Granted, (await first).Body.Status());
			Assert.Equal(200, await _server.PostAsync("notifications/app-store", AppleNotification("DID_RENEW", later, Renewal(), 1)));
			Assert.Equal("account-a", AppleRow.AccountId);

			// 더 앞 기간의 거래를 다른 계정이 늦게 지급해도 지금 기간의 소유자는 그대로다
			var oldest = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(-31).ToUnixTimeMilliseconds(),
				purchaseDate: IapTestServer.Now.AddDays(-61).ToUnixTimeMilliseconds());
			Assert.Equal(IapRedeemStatus.Granted, (await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "vip_month", "2000000555",
				_signer.Sign(oldest)), "account-c")).Body.Status());

			Assert.Equal("account-a", AppleRow.AccountId);
			Assert.Equal("2000000557", AppleRow.LatestTransactionId);
		}

		[Fact]
		public async Task Fake_store_subscription_is_delivered_once()
		{
			// 가짜 스토어도 지급한 거래로 스냅숏을 넘긴다(에디터에서 같은 지급자·처리기를 쓸 때 권한이 빠지지 않게, 리뷰 2b 8회차)
			var (_, granted) = await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.Fake, "vip_month", "fake-sub-1"));
			var (_, duplicate) = await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.Fake, "vip_month", "fake-sub-1"));

			Assert.Equal(IapRedeemStatus.Granted, granted.Status());
			Assert.Equal(IapRedeemCodes.Duplicate, duplicate.Code());
			var row = Assert.Single(_book.Deliveries);
			Assert.Equal(IapStoreNames.Fake, row.Store);
			Assert.Equal("fake-sub-1", row.SubscriptionId);
			Assert.Equal(IapSubscriptionState.Active, row.State);
			Assert.Equal(IapTestServer.DefaultAccount, row.AccountId);
			Assert.Equal(SubscriptionSync.Redeem, row.NotificationType);
		}

		[Theory]
		[InlineData("1000000000000001", false)]
		[InlineData("1000000000000002", true)]
		public async Task Owner_does_not_depend_on_when_a_later_notification_arrives(string subscriptionId, bool notificationFirst)
		{
			// A 가 T1, B 가 T2 를 지급받는다. T3 갱신 알림이 B 의 지급보다 먼저 오든 늦게 오든 소유자는 B 다(알림은 상태 기간만 올리고 소유권 기준은 지급, 리뷰 2b 9회차)
			var t1 = TestAppleSigner.Transaction(subscriptionId + "1", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(-31).ToUnixTimeMilliseconds(),
				purchaseDate: IapTestServer.Now.AddDays(-61).ToUnixTimeMilliseconds(), originalTransactionId: subscriptionId);
			var t2 = TestAppleSigner.Transaction(subscriptionId + "2", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds(),
				purchaseDate: IapTestServer.Now.AddDays(-31).ToUnixTimeMilliseconds(), originalTransactionId: subscriptionId);
			var t3 = TestAppleSigner.Transaction(subscriptionId + "3", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(29).ToUnixTimeMilliseconds(),
				purchaseDate: IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds(), originalTransactionId: subscriptionId);
			var renewal = AppleNotification("DID_RENEW", t3, Renewal(), 1);

			await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "vip_month", subscriptionId + "1", _signer.Sign(t1)), "account-a");

			if (notificationFirst)
			{
				Assert.Equal(200, await _server.PostAsync("notifications/app-store", renewal));
			}

			await _server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "vip_month", subscriptionId + "2", _signer.Sign(t2)), "account-b");

			if (!notificationFirst)
			{
				Assert.Equal(200, await _server.PostAsync("notifications/app-store", renewal));
			}

			var row = _book.Find(IapStoreNames.AppleAppStore, subscriptionId);
			Assert.Equal("account-b", row.AccountId);
			Assert.Equal(IapSubscriptionState.Active, row.State);
			Assert.Equal(subscriptionId + "3", row.LatestTransactionId);
		}

		[Fact]
		public async Task Subscription_without_a_purchase_time_is_refused_before_the_grant()
		{
			// 구매 시각이 없으면 기간 순서를 정할 수 없어 갱신이 버려진다. 검증기 오용이라 지급자를 부르기 전에 예외(500)로 드러낸다(리뷰 2b 9회차)
			var (status, _) = await _server.RedeemAsync(IapTestServer.Request(PeriodlessVerifier.Store, "vip_month", "custom-1"));

			Assert.Equal(500, status);
			Assert.Equal(0, _book.Grants);
			Assert.Null(await _server.Ledger.FindAsync(PeriodlessVerifier.Store, "custom-1", System.Threading.CancellationToken.None));
			Assert.Empty(_book.Deliveries);
		}

		private string AppleNotification(string type, Dictionary<string, object> transaction, Dictionary<string, object> renewal, int status, long? signedDate = null)
		{
			var data = new Dictionary<string, object>
			{
				{ "appAppleId", AppAppleId }, { "bundleId", TestAppleSigner.BundleId }, { "bundleVersion", "1.0" }, { "environment", "Production" },
				{ "signedTransactionInfo", _signer.Sign(transaction) }, { "signedRenewalInfo", _signer.Sign(renewal) }, { "status", status }
			};
			var payload = new Dictionary<string, object>
			{
				{ "notificationType", type }, { "notificationUUID", Guid.NewGuid().ToString() }, { "version", "2.0" }, { "signedDate", signedDate ?? SignedAt }, { "data", data }
			};
			return JsonSerializer.Serialize(new { signedPayload = _signer.Sign(payload) });
		}
	}

	/// <summary>구독 거래에 구매 시각을 넣지 않는 사용자 정의 검증기입니다(오용).</summary>
	internal sealed class PeriodlessVerifier : IStoreVerifier
	{
		public const string Store = "custom";

		public System.Collections.Generic.IReadOnlyCollection<string> Stores { get; } = new[] { Store };

		public string LedgerStore => Store;

		public Task<StoreVerification> VerifyAsync(IapRedeemRequest request, IapServerProduct product, System.Threading.CancellationToken cancellationToken)
		{
			return Task.FromResult(StoreVerification.Verified(new VerifiedPurchase
			{
				Store = Store, TransactionId = request.TransactionId, StoreProductId = product.Id, ProductType = product.Type,
				SubscriptionState = IapSubscriptionState.Active
			}));
		}

		public Task<bool> AcknowledgeAsync(PurchaseRecord record, IapServerProduct product, System.Threading.CancellationToken cancellationToken)
		{
			return Task.FromResult(true);
		}
	}
}
