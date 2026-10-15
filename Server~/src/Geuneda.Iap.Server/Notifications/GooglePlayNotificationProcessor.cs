using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// Google Play 실시간 개발자 알림을 처리합니다. 환불(voidedPurchaseNotification)은 원장의 누적 환불 수량으로, 구독 알림은 원장의 구독 잠금 안에서 Android Publisher API 로 지금 상태를
	/// 다시 읽어 알림 처리기에 넘깁니다(알림 내용만 믿지 않음). 지급하지 않은 결제는 확인(acknowledge)하지 않습니다(클라이언트가 끝내 지급받지 못하면 Google 자동 환불이 마지막 안전망).
	/// 모르는 값·불완전한 응답은 회수하지 않고 다시 보내 달라고 답합니다(잘못 회수·무시하면 되돌릴 수 없음).
	/// </summary>
	internal sealed class GooglePlayNotificationProcessor
	{
		private const int SubscriptionRevoked = 12;
		private const int OneTimeProduct = 2;
		private const int FullRefund = 1;

		private static readonly Dictionary<int, string> SubscriptionTypes = new Dictionary<int, string>
		{
			{ 1, "SUBSCRIPTION_RECOVERED" }, { 2, "SUBSCRIPTION_RENEWED" }, { 3, "SUBSCRIPTION_CANCELED" }, { 4, "SUBSCRIPTION_PURCHASED" },
			{ 5, "SUBSCRIPTION_ON_HOLD" }, { 6, "SUBSCRIPTION_IN_GRACE_PERIOD" }, { 7, "SUBSCRIPTION_RESTARTED" }, { 8, "SUBSCRIPTION_PRICE_CHANGE_CONFIRMED" },
			{ 9, "SUBSCRIPTION_DEFERRED" }, { 10, "SUBSCRIPTION_PAUSED" }, { 11, "SUBSCRIPTION_PAUSE_SCHEDULE_CHANGED" }, { 12, "SUBSCRIPTION_REVOKED" },
			{ 13, "SUBSCRIPTION_EXPIRED" }, { 17, "SUBSCRIPTION_ITEMS_CHANGED" }, { 18, "SUBSCRIPTION_CANCELLATION_SCHEDULED" },
			{ 19, "SUBSCRIPTION_PRICE_CHANGE_UPDATED" }, { 20, "SUBSCRIPTION_PENDING_PURCHASE_CANCELED" }, { 22, "SUBSCRIPTION_PRICE_STEP_UP_CONSENT_UPDATED" }
		};

		private readonly GooglePlayApi _api;
		private readonly IPurchaseLedger _ledger;
		private readonly IPurchaseNotificationHandler _handler;
		private readonly RefundApplier _refunds;
		private readonly IapProductCatalog _catalog;
		private readonly GooglePlayOptions _options;
		private readonly TimeProvider _time;
		private readonly ILogger<GooglePlayNotificationProcessor> _logger;

		public GooglePlayNotificationProcessor(GooglePlayApi api, IPurchaseLedger ledger, IPurchaseNotificationHandler handler, RefundApplier refunds,
			IapProductCatalog catalog, IOptions<GooglePlayOptions> options, TimeProvider time, ILogger<GooglePlayNotificationProcessor> logger)
		{
			_api = api;
			_ledger = ledger;
			_handler = handler;
			_refunds = refunds;
			_catalog = catalog;
			_options = options.Value;
			_time = time;
			_logger = logger;
		}

		public async Task<NotificationOutcome> ProcessAsync(GoogleDeveloperNotification notification, CancellationToken cancellationToken)
		{
			if (notification.PackageName != _options.PackageName)
			{
				_logger.LogWarning("Ignored a Google Play notification for package {Package}", notification.PackageName);
				return NotificationOutcome.Ignored;
			}

			var observedAt = notification.EventTimeMillis is { } millis ? DateTimeOffset.FromUnixTimeMilliseconds(millis) : _time.GetUtcNow();

			if (notification.VoidedPurchaseNotification != null)
			{
				return await VoidedAsync(notification.VoidedPurchaseNotification, observedAt, cancellationToken);
			}

			if (notification.SubscriptionNotification != null)
			{
				return await SubscriptionAsync(notification.SubscriptionNotification, observedAt, cancellationToken);
			}

			if (notification.OneTimeProductNotification != null)
			{
				// 지급은 클라이언트의 지급 요청으로 한다(계정을 아는 곳). 여기서 확인하면 지급받지 못한 결제의 자동 환불을 막아 버린다
				_logger.LogInformation("Google Play one-time product notification {Type} for {Sku}", notification.OneTimeProductNotification.NotificationType,
					notification.OneTimeProductNotification.Sku);
				return NotificationOutcome.Ignored;
			}

			_logger.LogInformation(notification.TestNotification != null ? "Received a Google Play test notification" : "Ignored an unknown Google Play notification");
			return NotificationOutcome.Ignored;
		}

		private async Task<NotificationOutcome> VoidedAsync(GoogleVoidedPurchaseNotification voided, DateTimeOffset observedAt, CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(voided.PurchaseToken))
			{
				_logger.LogWarning("Ignored a Google Play voided purchase without a purchase token");
				return NotificationOutcome.Ignored;
			}

			var subscription = voided.ProductType == GoogleVoidedPurchaseNotification.SubscriptionProduct;

			if (!subscription && voided.ProductType != OneTimeProduct ||
			    voided.RefundType != FullRefund && voided.RefundType != GoogleVoidedPurchaseNotification.PartialRefund)
			{
				_logger.LogError("Google Play voided purchase {Token} has unknown product type {ProductType} or refund type {RefundType}; Pub/Sub will retry",
					voided.PurchaseToken, voided.ProductType, voided.RefundType);
				return NotificationOutcome.RetryLater;
			}

			var record = await _ledger.FindAsync(IapStoreNames.GooglePlay, voided.PurchaseToken, cancellationToken);
			var partial = voided.RefundType == GoogleVoidedPurchaseNotification.PartialRefund;
			var reason = partial ? "voidedPurchase:partial" : "voidedPurchase:full";

			if (subscription)
			{
				// 구독 결제의 환불은 권한만 다시 맞춘다(철회됐으면 상태가 만료로 옴). 토큰 하나에 주문이 여럿이라 지급 전에는 처음 주문인지 알 수 없고,
				// 환불 뒤에도 기간이 남을 수 있다. 지급 회수는 철회(SUBSCRIPTION_REVOKED)에서 한다
				return await DeliverSubscriptionAsync(voided.PurchaseToken, "voidedPurchase", cancellationToken);
			}

			if (record != null && !partial)
			{
				await _refunds.ApplyAsync(Change(record.TransactionId, record.Quantity, observedAt, record.ProductId, record.StoreProductId, record.Quantity), reason,
					cancellationToken);
				return NotificationOutcome.Processed;
			}

			// 일부 환불은 알림에 수량이 없고, 원장에 없는 거래는 상품·수량을 모른다. 지금 구매 상태에서 읽는다
			var current = await _api.GetProductAsync(voided.PurchaseToken, cancellationToken);
			var lineItem = current.Status != GoogleApiStatus.Ok
				? null
				: current.Value.ProductLineItem?.Find(item => !string.IsNullOrEmpty(item?.ProductId) && (record == null || item.ProductId == record.StoreProductId));
			var refundable = lineItem?.ProductOfferDetails?.RefundableQuantity;

			if (lineItem == null || partial && refundable == null)
			{
				_logger.LogWarning("Could not read Google Play purchase {Token} for its refund; Pub/Sub will retry: {Message}", voided.PurchaseToken,
					current.Message ?? "no line item or refundable quantity");
				return NotificationOutcome.RetryLater;
			}

			var quantity = record?.Quantity ?? Math.Max(1, lineItem.ProductOfferDetails?.Quantity ?? 1);
			var productId = record?.ProductId ??
			                (_catalog.TryGetByStoreProductId(IapStoreNames.GooglePlay, lineItem.ProductId, out var product) ? product.Id : lineItem.ProductId);

			// 남은 환불 가능 수량은 조회 시점의 상태라 알림 시각으로 순서를 정할 수 없다. Google 은 환불을 취소하지 않으므로 누적 수량을 줄이지 않는다
			await _refunds.ApplyAsync(Change(voided.PurchaseToken, partial ? quantity - refundable.Value : quantity, observedAt, productId, lineItem.ProductId, quantity),
				reason, cancellationToken);
			return NotificationOutcome.Processed;
		}

		private async Task<NotificationOutcome> SubscriptionAsync(GoogleSubscriptionNotification notification, DateTimeOffset observedAt,
			CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(notification.PurchaseToken))
			{
				_logger.LogWarning("Ignored a Google Play subscription notification without a purchase token");
				return NotificationOutcome.Ignored;
			}

			var type = SubscriptionTypes.TryGetValue(notification.NotificationType, out var name) ? name : "SUBSCRIPTION_" + notification.NotificationType;

			if (notification.NotificationType == SubscriptionRevoked)
			{
				// 철회는 권한 해제와 환불이 함께다. 지급을 회수하고, 지급 전이면 환불 기록을 남겨 나중의 지급 요청을 닫는다(환불 알림도 오면 원장이 한 번만 반영)
				var record = await _ledger.FindAsync(IapStoreNames.GooglePlay, notification.PurchaseToken, cancellationToken);
				var productId = record?.ProductId;
				string storeProductId = record?.StoreProductId;

				if (record == null)
				{
					var current = await _api.GetSubscriptionAsync(notification.PurchaseToken, cancellationToken);
					var lineItem = current.Status == GoogleApiStatus.Ok ? current.Value.LineItems?.Find(item => !string.IsNullOrEmpty(item?.ProductId)) : null;

					if (lineItem == null)
					{
						return current.Status == GoogleApiStatus.NotFound ? NotificationOutcome.Ignored : NotificationOutcome.RetryLater;
					}

					storeProductId = lineItem.ProductId;
					productId = _catalog.TryGetByStoreProductId(IapStoreNames.GooglePlay, storeProductId, out var product) ? product.Id : storeProductId;
				}

				await _refunds.ApplyAsync(Change(notification.PurchaseToken, record?.Quantity ?? 1, observedAt, productId, storeProductId, record?.Quantity ?? 1), type,
					cancellationToken);
			}

			return await DeliverSubscriptionAsync(notification.PurchaseToken, type, cancellationToken);
		}

		/// <summary>
		/// 원장의 구독 잠금 안에서 지금 상태를 조회해 넘깁니다. 조회가 잠금 안이라 늦게 끝난 옛 조회가 새 상태를 덮지 못하고, 관측 시각은 저장된 것보다 늦게 맞춥니다.
		/// </summary>
		private async Task<NotificationOutcome> DeliverSubscriptionAsync(string token, string type, CancellationToken cancellationToken)
		{
			var outcome = NotificationOutcome.Processed;
			await _ledger.UpdateSubscriptionAsync(IapStoreNames.GooglePlay, token, async (stored, ct) =>
			{
				var result = await _api.GetSubscriptionAsync(token, ct);

				if (result.Status != GoogleApiStatus.Ok)
				{
					if (result.Status == GoogleApiStatus.NotFound)
					{
						_logger.LogWarning("Google Play subscription {Token} is no longer readable ({Type}): {Message}", token, type, result.Message);
					}

					outcome = result.Status == GoogleApiStatus.NotFound ? NotificationOutcome.Ignored : NotificationOutcome.RetryLater;
					return null;
				}

				var subscription = result.Value;

				if (!GooglePlayVerifier.TryMapSubscriptionState(subscription.SubscriptionState, out var state))
				{
					if (subscription.SubscriptionState == GooglePlayVerifier.PendingSubscription ||
					    subscription.SubscriptionState == GooglePlayVerifier.PendingSubscriptionCanceled)
					{
						// 결제 전 상태다. 권한이 생긴 적이 없어 알릴 것이 없다
						outcome = NotificationOutcome.Ignored;
						return null;
					}

					_logger.LogError("Google Play returned unknown subscription state {State} for {Token}; Pub/Sub will retry", subscription.SubscriptionState, token);
					outcome = NotificationOutcome.RetryLater;
					return null;
				}

				if (subscription.LineItems == null || subscription.LineItems.Count == 0 || subscription.LineItems.Exists(item => string.IsNullOrEmpty(item?.ProductId)))
				{
					_logger.LogError("Google Play returned subscription {Token} without line items; Pub/Sub will retry", token);
					outcome = NotificationOutcome.RetryLater;
					return null;
				}

				GoogleSubscriptionLineItem lineItem = null;
				IapServerProduct product = null;

				foreach (var item in subscription.LineItems)
				{
					lineItem ??= item;

					if (_catalog.TryGetByStoreProductId(IapStoreNames.GooglePlay, item.ProductId, out product))
					{
						lineItem = item;
						break;
					}
				}

				// 업그레이드·재가입한 새 구독은 클라이언트가 지급 요청하기 전이면 원장에 없다. 이전 구독의 계정, 그다음 앞서 저장된 계정을 쓴다
				var owner = await FindOwnerAsync(token, ct);

				if (owner == null && !string.IsNullOrEmpty(subscription.LinkedPurchaseToken))
				{
					owner = await FindOwnerAsync(subscription.LinkedPurchaseToken, ct);
				}

				// 조회는 잠금 안이라 지금이 가장 새로운 관측이다. 서버 간 시계 오차로 저장된 시각보다 이르지 않게 맞춘다
				var now = _time.GetUtcNow();
				var observedAt = stored != null && now <= stored.ObservedAt ? stored.ObservedAt.AddTicks(1) : now;
				return new SubscriptionUpdate
				{
					Store = IapStoreNames.GooglePlay,
					SubscriptionId = token,
					LinkedSubscriptionId = string.IsNullOrEmpty(subscription.LinkedPurchaseToken) ? null : subscription.LinkedPurchaseToken,
					StoreProductId = lineItem.ProductId,
					Product = product,
					AccountId = owner?.AccountId ?? stored?.AccountId,
					State = state,
					ExpiresAt = GooglePlayVerifier.ParseTime(lineItem.ExpiryTime),
					AutoRenewing = lineItem.AutoRenewingPlan?.AutoRenewEnabled,
					IsTest = subscription.TestPurchase != null,
					NotificationType = type,
					ObservedAt = observedAt,
					LatestTransactionId = string.IsNullOrEmpty(lineItem.LatestSuccessfulOrderId) ? null : lineItem.LatestSuccessfulOrderId
				};
			}, _handler.OnSubscriptionChangedAsync, cancellationToken);
			return outcome;
		}

		private async Task<PurchaseRecord> FindOwnerAsync(string token, CancellationToken cancellationToken)
		{
			var record = await _ledger.FindAsync(IapStoreNames.GooglePlay, token, cancellationToken);
			return record != null && record.IsGranted ? record : null;
		}

		private static RefundChange Change(string token, int refunded, DateTimeOffset observedAt, string productId, string storeProductId, int quantity)
		{
			return new RefundChange
			{
				Store = IapStoreNames.GooglePlay,
				TransactionId = token,
				RefundedQuantity = refunded,
				ChangedAt = observedAt,
				Mode = RefundUpdateMode.NeverDecrease,
				ProductId = productId,
				StoreProductId = storeProductId,
				Quantity = quantity
			};
		}
	}
}
