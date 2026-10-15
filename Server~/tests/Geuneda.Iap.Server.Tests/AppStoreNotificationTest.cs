using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>App Store Server Notifications V2 를 Apple 과 같은 모양의 테스트 인증서로 확인합니다.</summary>
	public sealed class AppStoreNotificationTest : IDisposable
	{
		private const string Route = "notifications/app-store";
		private const long AppAppleId = 1234567890;
		private static readonly long SignedAt = IapTestServer.Now.AddHours(1).ToUnixTimeMilliseconds();

		private readonly TestAppleSigner _signer = TestAppleSigner.Create();

		public void Dispose()
		{
			_signer.Dispose();
		}

		private Task<IapTestServer> StartAsync(Action<AppStoreOptions> configure = null, bool hooked = false, TestLogSink logs = null)
		{
			return IapTestServer.StartAsync(iap =>
				{
					iap.AddAppStore(options =>
					{
						options.BundleId = TestAppleSigner.BundleId;
						options.OnlineChecks = false;
						options.AppAppleId = AppAppleId;
						options.RootCertificates.Add(_signer.RootDer);
						configure?.Invoke(options);
					}).AddNotificationHandler<TestNotificationHandler>(ServiceLifetime.Singleton);

					if (hooked)
					{
						iap.AddLedger<HookedLedger>(ServiceLifetime.Singleton);
					}
				},
				services: services =>
				{
					if (logs != null)
					{
						services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
					}
				},
				map: app => app.MapGeunedaIapAppStoreNotifications("/iap"));
		}

		private string Notification(string type, Dictionary<string, object> transaction = null, Dictionary<string, object> renewal = null, int? status = null,
			string environment = "Production", long? signedDate = null, string bundleId = TestAppleSigner.BundleId, long appAppleId = AppAppleId,
			TestAppleSigner signer = null)
		{
			var data = new Dictionary<string, object> { { "appAppleId", appAppleId }, { "bundleId", bundleId }, { "bundleVersion", "1.0" }, { "environment", environment } };

			if (transaction != null)
			{
				data["signedTransactionInfo"] = (signer ?? _signer).Sign(transaction);
			}

			if (renewal != null)
			{
				data["signedRenewalInfo"] = (signer ?? _signer).Sign(renewal);
			}

			if (status != null)
			{
				data["status"] = status.Value;
			}

			var payload = new Dictionary<string, object>
			{
				{ "notificationType", type }, { "notificationUUID", Guid.NewGuid().ToString() }, { "version", "2.0" }, { "signedDate", signedDate ?? SignedAt },
				{ "data", data }
			};
			return JsonSerializer.Serialize(new { signedPayload = _signer.Sign(payload) });
		}

		private static Dictionary<string, object> Renewal(int autoRenew = 1, bool billingRetry = false, long? graceUntil = null)
		{
			var renewal = new Dictionary<string, object>
			{
				{ "originalTransactionId", "1000000000000001" }, { "productId", "com.test.vip.month" }, { "autoRenewStatus", autoRenew },
				{ "isInBillingRetryPeriod", billingRetry }, { "signedDate", SignedAt }
			};

			if (graceUntil != null)
			{
				renewal["gracePeriodExpiresDate"] = graceUntil.Value;
			}

			return renewal;
		}

		[Fact]
		public async Task Refund_revokes_once_and_reversal_restores()
		{
			await using var server = await StartAsync();
			await server.GrantAsync(IapStoreNames.AppleAppStore, "2000000123", "gem_100", account: "account-7", storeProductId: "com.test.gem100");
			var refunded = TestAppleSigner.Transaction("2000000123", "com.test.gem100", revocationDate: SignedAt - 1000);

			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND", refunded)));
			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND", refunded)));
			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND_REVERSED", TestAppleSigner.Transaction("2000000123", "com.test.gem100"),
				signedDate: SignedAt + 60_000)));
			// 앞선 환불 알림이 다시 와도(더 오래된 이벤트) 반영하지 않는다
			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND", refunded)));

			var refund = Assert.Single(server.Notifications.Refunds);
			Assert.Equal("account-7", refund.AccountId);
			Assert.Equal("REFUND", refund.Reason);
			Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(SignedAt - 1000), refund.ObservedAt);
			var reversal = Assert.Single(server.Notifications.Reversals);
			Assert.Equal(1, reversal.Quantity);
			Assert.Equal(0, reversal.RefundedQuantity);
			Assert.Empty(server.Notifications.Subscriptions);
		}

		[Fact]
		public async Task Revoke_revokes_and_unknown_transactions_are_ignored()
		{
			await using var server = await StartAsync();
			await server.GrantAsync(IapStoreNames.AppleAppStore, "2000000123", "no_ads", storeProductId: "com.test.noads");

			Assert.Equal(200, await server.PostAsync(Route, Notification("REVOKE", TestAppleSigner.Transaction("2000000123", "com.test.noads", revocationDate: SignedAt))));
			Assert.Equal("REVOKE", Assert.Single(server.Notifications.Refunds).Reason);
		}

		[Fact]
		public async Task Refund_before_the_grant_blocks_a_replayed_pre_refund_signature()
		{
			// 구매 → 환불 → 환불 전에 받은 서명을 나중에 내도 지급하지 않는다(서명은 여전히 유효함, 리뷰 2b 2회차)
			await using var server = await StartAsync();
			var beforeRefund = _signer.Sign(TestAppleSigner.Transaction("2000000999", "com.test.gem100"));

			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND", TestAppleSigner.Transaction("2000000999", "com.test.gem100", revocationDate: SignedAt))));
			var (_, body) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "gem_100", "2000000999", beforeRefund));

			Assert.Equal(IapRedeemStatus.AlreadyGranted, body.Status());
			Assert.Equal(IapRedeemCodes.Revoked, body.Code());
			Assert.Equal(0, server.Grantor.Calls);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task Refund_reversed_after_a_closed_redeem_is_left_to_support(bool refundedSignature)
		{
			// 모두 환불된 거래의 지급 요청은 환불 전·후 서명 모두 계정을 기록하지 않고 닫는다. 지급하지 않은 계정이 구독 소유자·환불 취소 수취인이 되지 않고,
			// 나중의 환불 취소는 고객 지원이 Warning 로그(지급 요청의 계정, 환불 취소의 거래)로 지급한다(리뷰 2b 4·5회차, 사용자 결정)
			var logs = new TestLogSink();
			await using var server = await StartAsync(logs: logs);
			var refunded = TestAppleSigner.Transaction("2000000999", "com.test.gem100", revocationDate: SignedAt);
			var signature = _signer.Sign(refundedSignature ? refunded : TestAppleSigner.Transaction("2000000999", "com.test.gem100"));

			await server.PostAsync(Route, Notification("REFUND", refunded));
			var (_, closed) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "gem_100", "2000000999", signature), "account-7");
			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND_REVERSED", TestAppleSigner.Transaction("2000000999", "com.test.gem100"),
				signedDate: SignedAt + 60_000)));

			Assert.Equal(IapRedeemCodes.Revoked, closed.Code());
			Assert.Equal(0, server.Grantor.Calls);
			Assert.Empty(server.Notifications.Reversals);
			var record = await server.Ledger.FindAsync(IapStoreNames.AppleAppStore, "2000000999", System.Threading.CancellationToken.None);
			Assert.False(record.IsGranted);
			Assert.Equal(0, record.RefundedQuantity);
			Assert.Null(await server.Ledger.FindByOriginalTransactionAsync(IapStoreNames.AppleAppStore, "1000000000000001", System.Threading.CancellationToken.None));
			Assert.True(logs.Contains("2000000999", "account-7", "without granting"));
			Assert.True(logs.Contains("2000000999", "reversed the refund"));
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task Subscription_notification_without_a_purchase_date_is_retried(bool hasSnapshot)
		{
			// 구매 시각은 결제 기간의 기준이라 없으면 오래된 알림으로 버려진다. 상태를 바꾸지 않고 503 으로 다시 받는다(리뷰 2b 10회차)
			await using var server = await StartAsync();
			var transaction = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds());

			if (hasSnapshot)
			{
				Assert.Equal(200, await server.PostAsync(Route, Notification("DID_RENEW", transaction, Renewal(), 1)));
			}

			var undated = new Dictionary<string, object>(transaction);
			undated.Remove("purchaseDate");
			var grace = IapTestServer.Now.AddDays(3).ToUnixTimeMilliseconds();

			Assert.Equal(503, await server.PostAsync(Route, Notification("DID_FAIL_TO_RENEW", undated, Renewal(billingRetry: true, graceUntil: grace), 4,
				signedDate: SignedAt + 60_000)));
			Assert.Equal(hasSnapshot ? 1 : 0, server.Notifications.Subscriptions.Count);
		}

		[Fact]
		public async Task Older_notification_of_the_same_period_is_ignored()
		{
			// 같은 결제 기간(같은 거래) 안에서는 늦게 서명된 알림이 이긴다. 먼저 서명된 결제 재시도 알림이 늦게 와도 이용 중을 되돌리지 않는다
			await using var server = await StartAsync();
			var transaction = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(30).ToUnixTimeMilliseconds());

			Assert.Equal(200, await server.PostAsync(Route, Notification("DID_RENEW", transaction, Renewal(), 1, signedDate: SignedAt + 60_000)));
			Assert.Equal(200, await server.PostAsync(Route, Notification("DID_FAIL_TO_RENEW", transaction, Renewal(billingRetry: true), 3)));

			Assert.Equal(IapSubscriptionState.Active, Assert.Single(server.Notifications.Subscriptions).State);
		}

		[Fact]
		public async Task Subscription_owner_is_read_inside_the_subscription_lock()
		{
			// 잠금 밖에서 읽은 옛 소유자가, 그사이 다른 알림이 저장한 새 소유자를 덮지 않는다(리뷰 2b 4회차 — 잠금 밖 조회로 되돌리면 [b, a] 로 실패)
			await using var server = await StartAsync(hooked: true);
			var ledger = (HookedLedger) server.Services.GetRequiredService<IPurchaseLedger>();
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			ledger.HoldFirstSubscriptionUpdate = release.Task;
			await server.GrantAsync(IapStoreNames.AppleAppStore, "1000000000000001", "vip_month", account: "account-a", storeProductId: "com.test.vip.month");
			var expires = IapTestServer.Now.AddDays(30).ToUnixTimeMilliseconds();

			var newer = server.PostAsync(Route, Notification("DID_RENEW", TestAppleSigner.Transaction("2000000556", "com.test.vip.month", expiresDate: expires),
				Renewal(), 1, signedDate: SignedAt + 60_000));
			await ledger.FirstSubscriptionUpdateEntered.Task;
			await server.GrantAsync(IapStoreNames.AppleAppStore, "2000000555", "vip_month", account: "account-b", storeProductId: "com.test.vip.month",
				originalTransactionId: "1000000000000001", redeemedAt: IapTestServer.Now.AddMinutes(1));
			Assert.Equal(200, await server.PostAsync(Route, Notification("DID_RENEW", TestAppleSigner.Transaction("2000000555", "com.test.vip.month",
				expiresDate: expires), Renewal(), 1)));
			release.SetResult(true);

			Assert.Equal(200, await newer);
			Assert.Equal(new[] { "account-b", "account-b" }, server.Notifications.Subscriptions.Select(update => update.AccountId));
		}

		[Theory]
		[InlineData(0)]
		[InlineData(99)]
		public async Task Unknown_subscription_status_is_retried_without_a_change(int status)
		{
			await using var server = await StartAsync();
			var transaction = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds());

			Assert.Equal(503, await server.PostAsync(Route, Notification("DID_RENEW", transaction, Renewal(), status)));
			Assert.Empty(server.Notifications.Subscriptions);
		}

		[Fact]
		public async Task Refund_reversed_before_the_grant_lets_the_grant_through()
		{
			await using var server = await StartAsync();
			var signature = _signer.Sign(TestAppleSigner.Transaction("2000000999", "com.test.gem100"));

			await server.PostAsync(Route, Notification("REFUND", TestAppleSigner.Transaction("2000000999", "com.test.gem100", revocationDate: SignedAt)));
			await server.PostAsync(Route, Notification("REFUND_REVERSED", TestAppleSigner.Transaction("2000000999", "com.test.gem100"), signedDate: SignedAt + 60_000));
			var (_, body) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "gem_100", "2000000999", signature));

			Assert.Equal(IapRedeemStatus.Granted, body.Status());
			Assert.Equal(1, Assert.Single(server.Grantor.Grants).Quantity);
		}

		[Fact]
		public async Task Reversal_arriving_first_wins_over_a_later_delivered_older_refund()
		{
			await using var server = await StartAsync();
			await server.GrantAsync(IapStoreNames.AppleAppStore, "2000000123", "gem_100", storeProductId: "com.test.gem100");

			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND_REVERSED", TestAppleSigner.Transaction("2000000123", "com.test.gem100"),
				signedDate: SignedAt + 60_000)));
			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND",
				TestAppleSigner.Transaction("2000000123", "com.test.gem100", revocationDate: SignedAt - 1000))));

			Assert.Empty(server.Notifications.Refunds);
			Assert.Empty(server.Notifications.Reversals);
		}

		[Fact]
		public async Task Refund_during_an_uncommitted_grant_waits_for_the_commit_and_revokes()
		{
			await using var server = await StartAsync();
			var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			server.Grantor.Enqueue(async (grant, token) =>
			{
				entered.SetResult(true);
				await release.Task;
				return GrantResult.Granted(new TestReward { Gems = 100 });
			});
			var jws = _signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem100"));
			var refund = Notification("REFUND", TestAppleSigner.Transaction("2000000123", "com.test.gem100", revocationDate: SignedAt));

			var redeem = server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "gem_100", "2000000123", jws));
			await entered.Task;
			var push = server.PostAsync(Route, refund);
			await Task.Delay(200);
			Assert.False(push.IsCompleted);
			release.SetResult(true);

			Assert.Equal(IapRedeemStatus.Granted, (await redeem).Body.Status());
			Assert.Equal(200, await push);
			Assert.Single(server.Notifications.Refunds);
		}

		[Fact]
		public async Task Refund_of_an_unredeemed_renewal_still_reports_the_subscription_state()
		{
			// 갱신 거래는 클라이언트가 지급 요청하기 전이라 원장에 없지만, 구독 권한 해제는 바로 전달돼야 한다(리뷰 2b 2회차)
			await using var server = await StartAsync();
			await server.GrantAsync(IapStoreNames.AppleAppStore, "1000000000000001", "vip_month", account: "account-7", storeProductId: "com.test.vip.month");
			var renewal = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(30).ToUnixTimeMilliseconds());
			var refunded = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(30).ToUnixTimeMilliseconds(),
				revocationDate: SignedAt + 1000);

			Assert.Equal(200, await server.PostAsync(Route, Notification("DID_RENEW", renewal, Renewal(), 1)));
			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND", refunded, Renewal(autoRenew: 0), 5, signedDate: SignedAt + 2000)));

			Assert.Equal(new[] { IapSubscriptionState.Active, IapSubscriptionState.Expired }, server.Notifications.Subscriptions.Select(update => update.State));
			Assert.All(server.Notifications.Subscriptions, update => Assert.Equal("account-7", update.AccountId));
			Assert.Empty(server.Notifications.Refunds);
			Assert.False((await server.Ledger.FindAsync(IapStoreNames.AppleAppStore, "2000000555", System.Threading.CancellationToken.None)).IsGranted);
		}

		[Fact]
		public async Task Subscription_granted_through_a_renewal_finds_its_owner()
		{
			// 처음 거래(원래 거래 ID)는 원장에 없고 갱신 거래만 지급한 구독의 다음 갱신 알림(리뷰 2b 1회차)
			await using var server = await StartAsync();
			await server.GrantAsync(IapStoreNames.AppleAppStore, "2000000444", "vip_month", account: "account-7", storeProductId: "com.test.vip.month",
				originalTransactionId: "1000000000000001");
			var next = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: IapTestServer.Now.AddDays(30).ToUnixTimeMilliseconds());

			Assert.Equal(200, await server.PostAsync(Route, Notification("DID_RENEW", next, Renewal(), 1)));

			Assert.Equal("account-7", Assert.Single(server.Notifications.Subscriptions).AccountId);
		}

		[Theory]
		[InlineData(1, 1, IapSubscriptionState.Active)]
		[InlineData(1, 0, IapSubscriptionState.Canceled)]
		[InlineData(2, 0, IapSubscriptionState.Expired)]
		[InlineData(3, 1, IapSubscriptionState.OnHold)]
		[InlineData(4, 1, IapSubscriptionState.GracePeriod)]
		[InlineData(5, 0, IapSubscriptionState.Expired)]
		public async Task Subscription_status_is_reported_to_the_original_owner(int status, int autoRenew, IapSubscriptionState expected)
		{
			await using var server = await StartAsync();
			await server.GrantAsync(IapStoreNames.AppleAppStore, "1000000000000001", "vip_month", account: "account-7", storeProductId: "com.test.vip.month");
			var expires = IapTestServer.Now.AddDays(30).ToUnixTimeMilliseconds();
			var renewal = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: expires);

			Assert.Equal(200, await server.PostAsync(Route, Notification("DID_RENEW", renewal, Renewal(autoRenew), status)));

			var update = Assert.Single(server.Notifications.Subscriptions);
			Assert.Equal(expected, update.State);
			Assert.Equal("account-7", update.AccountId);
			Assert.Equal("1000000000000001", update.SubscriptionId);
			Assert.Equal("2000000555", update.LatestTransactionId);
			Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(expires), update.ExpiresAt);
			Assert.Equal(autoRenew == 1, update.AutoRenewing);
			Assert.Equal("vip_month", update.Product.Id);
			Assert.Equal("DID_RENEW", update.NotificationType);
			Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(SignedAt), update.ObservedAt);
			Assert.False(update.IsTest);
		}

		[Fact]
		public async Task Subscription_state_without_status_comes_from_expiry_and_renewal_info()
		{
			await using var server = await StartAsync();
			var expired = IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds();
			var grace = IapTestServer.Now.AddDays(3).ToUnixTimeMilliseconds();
			var transaction = TestAppleSigner.Transaction("2000000555", "com.test.vip.month", expiresDate: expired);

			await server.PostAsync(Route, Notification("DID_FAIL_TO_RENEW", transaction, Renewal(billingRetry: true, graceUntil: grace)));
			await server.PostAsync(Route, Notification("DID_FAIL_TO_RENEW", transaction, Renewal(billingRetry: true)));
			await server.PostAsync(Route, Notification("EXPIRED", transaction, Renewal(autoRenew: 0)));

			Assert.Equal(new[] { IapSubscriptionState.GracePeriod, IapSubscriptionState.OnHold, IapSubscriptionState.Expired },
				server.Notifications.Subscriptions.Select(update => update.State));
			Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(grace), server.Notifications.Subscriptions.First().GracePeriodExpiresAt);
			Assert.All(server.Notifications.Subscriptions, update => Assert.Null(update.AccountId));
		}

		[Fact]
		public async Task Unsigned_or_foreign_signed_notifications_are_refused()
		{
			using var stranger = TestAppleSigner.Create();
			await using var server = await StartAsync();
			await server.GrantAsync(IapStoreNames.AppleAppStore, "2000000123", "gem_100", storeProductId: "com.test.gem100");
			var refunded = TestAppleSigner.Transaction("2000000123", "com.test.gem100", revocationDate: SignedAt);
			var foreignOuter = JsonSerializer.Serialize(new { signedPayload = stranger.Sign(new Dictionary<string, object> { { "notificationType", "REFUND" } }) });

			Assert.Equal(401, await server.PostAsync(Route, foreignOuter));
			Assert.Equal(401, await server.PostAsync(Route, Notification("REFUND", refunded, signer: stranger)));
			Assert.Equal(400, await server.PostAsync(Route, "{}"));
			Assert.Equal(400, await server.PostAsync(Route, "not json"));
			Assert.Empty(server.Notifications.Refunds);
		}

		[Fact]
		public async Task Other_app_environment_and_test_notifications_are_ignored()
		{
			await using var server = await StartAsync(options => options.AllowSandbox = false);
			await server.GrantAsync(IapStoreNames.AppleAppStore, "2000000123", "gem_100", storeProductId: "com.test.gem100");
			var refunded = TestAppleSigner.Transaction("2000000123", "com.test.gem100", revocationDate: SignedAt);

			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND", refunded, bundleId: "com.other")));
			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND", refunded, environment: "Sandbox")));
			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND", refunded, appAppleId: 42)));
			Assert.Equal(200, await server.PostAsync(Route, Notification("TEST")));
			Assert.Empty(server.Notifications.Refunds);

			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND", refunded)));
			Assert.Single(server.Notifications.Refunds);
		}

		[Fact]
		public async Task Handler_failure_is_a_server_error_and_retries_apply_once()
		{
			await using var server = await StartAsync();
			await server.GrantAsync(IapStoreNames.AppleAppStore, "2000000123", "gem_100", storeProductId: "com.test.gem100");
			var refund = Notification("REFUND", TestAppleSigner.Transaction("2000000123", "com.test.gem100", revocationDate: SignedAt));
			server.Notifications.ThrowOnce(new InvalidOperationException("database is down"));

			Assert.Equal(500, await server.PostAsync(Route, refund));
			Assert.Equal(200, await server.PostAsync(Route, refund));
			Assert.Equal(200, await server.PostAsync(Route, refund));

			Assert.Single(server.Notifications.Refunds);
		}

		[Fact]
		public async Task Unknown_revocation_status_is_retried()
		{
			await using var server = await StartAsync(options => options.OnlineChecks = true);

			Assert.Equal(503, await server.PostAsync(Route, Notification("TEST")));
		}
	}
}
