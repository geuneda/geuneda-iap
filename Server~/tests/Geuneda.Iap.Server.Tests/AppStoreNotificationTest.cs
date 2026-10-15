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

		private Task<IapTestServer> StartAsync(Action<AppStoreOptions> configure = null)
		{
			return IapTestServer.StartAsync(iap => iap.AddAppStore(options =>
				{
					options.BundleId = TestAppleSigner.BundleId;
					options.OnlineChecks = false;
					options.AppAppleId = AppAppleId;
					options.RootCertificates.Add(_signer.RootDer);
					configure?.Invoke(options);
				}).AddNotificationHandler<TestNotificationHandler>(ServiceLifetime.Singleton),
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

		[Fact]
		public async Task Refund_reversed_after_a_closed_redeem_restores_the_account()
		{
			// 모두 환불된 거래의 지급 요청은 지급 없이 닫지만 계정은 기록해, 나중에 환불이 취소되면 그 계정에 돌려준다(리뷰 2b 3회차)
			await using var server = await StartAsync();
			var beforeRefund = _signer.Sign(TestAppleSigner.Transaction("2000000999", "com.test.gem100"));

			await server.PostAsync(Route, Notification("REFUND", TestAppleSigner.Transaction("2000000999", "com.test.gem100", revocationDate: SignedAt)));
			var (_, closed) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "gem_100", "2000000999", beforeRefund), "account-7");
			var (_, again) = await server.RedeemAsync(IapTestServer.Request(IapStoreNames.AppleAppStore, "gem_100", "2000000999", beforeRefund), "account-7");
			Assert.Equal(200, await server.PostAsync(Route, Notification("REFUND_REVERSED", TestAppleSigner.Transaction("2000000999", "com.test.gem100"),
				signedDate: SignedAt + 60_000)));

			Assert.Equal(IapRedeemCodes.Revoked, closed.Code());
			Assert.Equal(IapRedeemCodes.Revoked, again.Code());
			Assert.Equal(0, server.Grantor.Calls);
			var reversal = Assert.Single(server.Notifications.Reversals);
			Assert.Equal("account-7", reversal.AccountId);
			Assert.Equal(1, reversal.Quantity);
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
