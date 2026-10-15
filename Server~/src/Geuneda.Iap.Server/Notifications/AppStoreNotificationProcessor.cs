using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// App Store Server Notifications V2 를 처리합니다. 알림·거래·갱신 정보 서명을 모두 검증하고(<see cref="AppleSignedDataVerifier"/>), 번들 ID·환경·앱 ID 를 확인합니다.
	/// REFUND·REVOKE 는 원장의 거래를 모두 환불로, REFUND_REVERSED 는 환불 없음으로 맞추고, 구독 상품이면 알림의 상태를 알림 처리기에 넘깁니다.
	/// </summary>
	internal sealed class AppStoreNotificationProcessor
	{
		private const string Refund = "REFUND";
		private const string Revoke = "REVOKE";
		private const string RefundReversed = "REFUND_REVERSED";
		private const string Test = "TEST";

		private readonly AppleSignedDataVerifier _signed;
		private readonly IPurchaseLedger _ledger;
		private readonly IPurchaseNotificationHandler _handler;
		private readonly RefundApplier _refunds;
		private readonly IapProductCatalog _catalog;
		private readonly AppStoreOptions _options;
		private readonly TimeProvider _time;
		private readonly ILogger<AppStoreNotificationProcessor> _logger;

		public AppStoreNotificationProcessor(AppleSignedDataVerifier signed, IPurchaseLedger ledger, IPurchaseNotificationHandler handler, RefundApplier refunds,
			IapProductCatalog catalog, IOptions<AppStoreOptions> options, TimeProvider time, ILogger<AppStoreNotificationProcessor> logger)
		{
			_signed = signed;
			_ledger = ledger;
			_handler = handler;
			_refunds = refunds;
			_catalog = catalog;
			_options = options.Value;
			_time = time;
			_logger = logger;
		}

		public async Task<NotificationOutcome> ProcessAsync(string signedPayload, CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(signedPayload))
			{
				_logger.LogWarning("Refused an App Store notification without signedPayload");
				return NotificationOutcome.BadRequest;
			}

			var (outcome, payload) = Decode<AppleNotificationPayload>(signedPayload, "notification");

			if (payload == null)
			{
				return outcome;
			}

			var data = payload.Data;

			if (data == null)
			{
				// 갱신 연장 요약(RENEWAL_EXTENSION SUMMARY), 외부 결제 토큰 알림
				_logger.LogInformation("Ignored App Store notification {Type} {Subtype} without app data", payload.NotificationType, payload.Subtype);
				return NotificationOutcome.Ignored;
			}

			var refusal = CheckApp(data);

			if (refusal != null)
			{
				_logger.LogWarning("Ignored App Store notification {Type} ({Uuid}): {Reason}", payload.NotificationType, payload.NotificationUuid, refusal);
				return NotificationOutcome.Ignored;
			}

			if (payload.NotificationType == Test)
			{
				_logger.LogInformation("Received an App Store test notification ({Environment})", data.Environment);
				return NotificationOutcome.Ignored;
			}

			var (transactionOutcome, transaction) = Decode<AppleTransactionPayload>(data.SignedTransactionInfo, "transaction");

			if (transaction == null)
			{
				if (transactionOutcome != NotificationOutcome.Ignored)
				{
					return transactionOutcome;
				}

				_logger.LogInformation("Ignored App Store notification {Type} without a transaction", payload.NotificationType);
				return NotificationOutcome.Ignored;
			}

			var (renewalOutcome, renewal) = Decode<AppleRenewalPayload>(data.SignedRenewalInfo, "renewal info");

			if (renewalOutcome != NotificationOutcome.Ignored && renewalOutcome != NotificationOutcome.Processed)
			{
				return renewalOutcome;
			}

			var observedAt = payload.SignedDate is { } signedDate ? DateTimeOffset.FromUnixTimeMilliseconds(signedDate) : _time.GetUtcNow();
			_catalog.TryGetByStoreProductId(IapStoreNames.AppleAppStore, transaction.ProductId, out var product);

			if (payload.NotificationType == Refund || payload.NotificationType == Revoke || payload.NotificationType == RefundReversed)
			{
				// 서명된 이벤트라 이벤트 시각으로 순서를 정한다(환불 취소가 먼저 와도 늦게 온 이전 환불은 반영하지 않음).
				// 지급 전의 거래면 원장이 환불 기록으로 남겨, 환불 전 서명을 나중에 내도 지급되지 않는다
				var record = await _ledger.FindAsync(IapStoreNames.AppleAppStore, transaction.TransactionId, cancellationToken);
				var reversed = payload.NotificationType == RefundReversed;
				var quantity = record?.Quantity ?? Math.Max(1, transaction.Quantity ?? 1);
				var refundOutcome = await _refunds.ApplyAsync(new RefundChange
				{
					Store = IapStoreNames.AppleAppStore,
					TransactionId = transaction.TransactionId,
					RefundedQuantity = reversed ? 0 : quantity,
					ChangedAt = !reversed && transaction.RevocationDate is { } revoked ? DateTimeOffset.FromUnixTimeMilliseconds(revoked) : observedAt,
					Mode = RefundUpdateMode.EventOrdered,
					ProductId = record?.ProductId ?? product?.Id ?? transaction.ProductId,
					StoreProductId = transaction.ProductId,
					Quantity = quantity
				}, payload.NotificationType, cancellationToken);

				if (reversed && refundOutcome == LedgerRefundOutcome.RecordedBeforeGrant)
				{
					// 모두 환불된 결제의 지급 요청은 계정을 기록하지 않고 닫으므로(Revoked) 돌려줄 계정을 모른다. 아직 지급 요청 전이면 그 요청이 지급한다
					_logger.LogWarning("App Store reversed the refund of transaction {TransactionId} ({ProductId}) that was never granted. If the client already " +
					                   "closed it as refunded, grant it through support (the 'Closed ... without granting' warning names the account)", transaction.TransactionId,
						transaction.ProductId);
				}
			}

			if (renewal == null && product?.Type != IapProductType.Subscription)
			{
				return NotificationOutcome.Processed;
			}

			if (data.Status is { } status && (status < 1 || status > 5))
			{
				// 모르는 상태를 추정해 저장하면 되돌릴 수 없다. 상태를 바꾸지 않고 Apple 이 다시 보내게 한다
				_logger.LogError("App Store notification {Type} has unknown subscription status {Status}; Apple will retry", payload.NotificationType, status);
				return NotificationOutcome.RetryLater;
			}

			if (transaction.PurchaseDate == null)
			{
				// 구매 시각이 결제 기간의 기준이다. 없으면 오래된 알림으로 버려지므로(받은 시각으로 대신하면 옛 거래가 최신 기간이 됨) 상태를 바꾸지 않고 Apple 이 다시 보내게 한다
				_logger.LogError("App Store notification {Type} has a subscription transaction {TransactionId} without purchaseDate; Apple will retry", payload.NotificationType,
					transaction.TransactionId);
				return NotificationOutcome.RetryLater;
			}

			var subscriptionId = string.IsNullOrEmpty(transaction.OriginalTransactionId) ? transaction.TransactionId : transaction.OriginalTransactionId;

			var update = new SubscriptionUpdate
			{
				Store = IapStoreNames.AppleAppStore,
				SubscriptionId = subscriptionId,
				StoreProductId = transaction.ProductId,
				Product = product,
				State = SubscriptionState(data.Status, transaction, renewal),
				ExpiresAt = transaction.ExpiresDate is { } expires ? DateTimeOffset.FromUnixTimeMilliseconds(expires) : null,
				GracePeriodExpiresAt = renewal?.GracePeriodExpiresDate is { } grace ? DateTimeOffset.FromUnixTimeMilliseconds(grace) : null,
				AutoRenewing = renewal?.AutoRenewStatus is { } autoRenew ? autoRenew == 1 : null,
				IsTest = data.Environment == AppStoreVerifier.Sandbox,
				NotificationType = string.IsNullOrEmpty(payload.Subtype) ? payload.NotificationType : payload.NotificationType + ":" + payload.Subtype,
				ObservedAt = observedAt,
				PeriodStartedAt = DateTimeOffset.FromUnixTimeMilliseconds(transaction.PurchaseDate.Value),
				LatestTransactionId = transaction.TransactionId
			};
			await _ledger.UpdateSubscriptionAsync(IapStoreNames.AppleAppStore, subscriptionId, async (stored, ct) =>
			{
				// 소유 계정은 스냅숏이 정한다(구독 지급이 구독 잠금 안에서 정함). 스냅숏에 계정이 없을 때만 원래 거래 ID 로 지급한 가장 최근 기록에서 찾는다
				// (지급 시작 시각 순인 원장 조회로 잠금 순서인 소유자를 덮지 않게)
				var owner = stored?.AccountId == null ? await _ledger.FindByOriginalTransactionAsync(IapStoreNames.AppleAppStore, subscriptionId, ct) : null;
				var merged = SubscriptionMerge.ByPeriod(stored, update with
				{
					AccountId = stored?.AccountId ?? owner?.AccountId,
					AccountPeriodStartedAt = stored?.AccountId != null ? stored.AccountPeriodStartedAt : owner?.PurchasedAt
				}, false);

				if (merged == null)
				{
					_logger.LogInformation("Ignored an older App Store subscription state for {SubscriptionId} ({Type}, transaction {TransactionId})", subscriptionId,
						payload.NotificationType, transaction.TransactionId);
				}

				return merged;
			}, _handler.OnSubscriptionChangedAsync, cancellationToken);

			return NotificationOutcome.Processed;
		}

		/// <summary>번들 ID, 환경, 운영 환경의 앱 ID 를 확인합니다. 맞으면 null, 아니면 이유입니다.</summary>
		private string CheckApp(AppleNotificationData data)
		{
			if (data.BundleId != _options.BundleId)
			{
				return $"bundle {data.BundleId}";
			}

			switch (data.Environment)
			{
				case AppStoreVerifier.Production:
					return _options.AppAppleId != null && data.AppAppleId != _options.AppAppleId ? $"app apple id {data.AppAppleId}" : null;
				case AppStoreVerifier.Sandbox:
					return _options.AllowSandbox ? null : "sandbox is not allowed";
				default:
					return $"environment {data.Environment}";
			}
		}

		/// <summary>
		/// 알림의 구독 상태(<c>status</c>)를 옮깁니다. 없으면 거래의 만료 시각과 갱신 정보로 정합니다.
		/// </summary>
		private IapSubscriptionState SubscriptionState(int? status, AppleTransactionPayload transaction, AppleRenewalPayload renewal)
		{
			var autoRenewOff = renewal?.AutoRenewStatus == 0;

			switch (status)
			{
				case 1:
					return autoRenewOff ? IapSubscriptionState.Canceled : IapSubscriptionState.Active;
				case 2:
				case 5:
					return IapSubscriptionState.Expired;
				case 3:
					return IapSubscriptionState.OnHold;
				case 4:
					return IapSubscriptionState.GracePeriod;
			}

			var now = _time.GetUtcNow();

			if (transaction.RevocationDate == null && transaction.ExpiresDate is { } expires && DateTimeOffset.FromUnixTimeMilliseconds(expires) > now)
			{
				return autoRenewOff ? IapSubscriptionState.Canceled : IapSubscriptionState.Active;
			}

			if (renewal?.IsInBillingRetryPeriod == true)
			{
				return renewal.GracePeriodExpiresDate is { } grace && DateTimeOffset.FromUnixTimeMilliseconds(grace) > now
					? IapSubscriptionState.GracePeriod
					: IapSubscriptionState.OnHold;
			}

			return IapSubscriptionState.Expired;
		}

		/// <summary>
		/// 서명 데이터를 검증해 읽습니다. 없으면 (Ignored, null), 서명이 틀리면 (Unauthorized, null), 폐기 확인을 못 하면 (RetryLater, null), 읽지 못하면 (BadRequest, null) 입니다.
		/// </summary>
		private (NotificationOutcome Outcome, T Value) Decode<T>(string jws, string what) where T : class
		{
			if (string.IsNullOrEmpty(jws))
			{
				return (NotificationOutcome.Ignored, null);
			}

			var verified = _signed.Verify(jws);

			switch (verified.Status)
			{
				case AppleSignedDataStatus.Valid:
					break;
				case AppleSignedDataStatus.Retryable:
					_logger.LogWarning("Could not check the App Store {What} certificates; Apple will retry: {Message}", what, verified.Message);
					return (NotificationOutcome.RetryLater, null);
				default:
					_logger.LogWarning("Refused an App Store notification {What}: {Message}", what, verified.Message);
					return (NotificationOutcome.Unauthorized, null);
			}

			try
			{
				var value = JsonSerializer.Deserialize<T>(verified.Payload, IapServerJson.Options);
				return value == null ? (NotificationOutcome.BadRequest, null) : (NotificationOutcome.Processed, value);
			}
			catch (JsonException e)
			{
				_logger.LogError(e, "Could not read the App Store notification {What}", what);
				return (NotificationOutcome.BadRequest, null);
			}
		}
	}
}
