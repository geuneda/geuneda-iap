using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// Google Play 검증기입니다. 구매 토큰(요청의 <c>TransactionId</c>)으로 Android Publisher API 를 조회합니다. 일회성 상품은 <c>productsv2</c>, 구독은 <c>subscriptionsv2</c> 입니다.
	/// </summary>
	/// <remarks>
	/// 일회성: PURCHASED 만 지급, PENDING 은 결제 대기, CANCELLED 는 지급 없이 닫음. 구독: PENDING 은 결제 대기, PENDING_PURCHASE_CANCELED 는 닫음, 그 밖(만료 포함)은
	/// 상태와 만료 시각을 지급자에 넘김. 상태 값을 모르거나 상품 목록이 없으면 거절하지 않고 다시 시도하게 합니다(잘못 닫으면 지급 누락).
	/// 지급 뒤 확인(acknowledge)은 결제 뒤 3일(<see cref="AcknowledgementWindow"/>) 안에만 다시 시도합니다(그 뒤에는 Google 이 이미 환불함).
	/// </remarks>
	internal sealed class GooglePlayVerifier : IStoreVerifier
	{
		/// <summary>Google 이 확인하지 않은 결제를 자동 환불하는 기한</summary>
		internal static readonly TimeSpan AcknowledgementWindow = TimeSpan.FromDays(3);

		private static readonly string[] StoreNames = { IapStoreNames.GooglePlay };

		private readonly GooglePlayApi _api;
		private readonly GooglePlayOptions _options;
		private readonly TimeProvider _time;
		private readonly ILogger<GooglePlayVerifier> _logger;

		public GooglePlayVerifier(GooglePlayApi api, IOptions<GooglePlayOptions> options, TimeProvider time, ILogger<GooglePlayVerifier> logger)
		{
			_api = api;
			_options = options.Value;
			_time = time;
			_logger = logger;
		}

		public IReadOnlyCollection<string> Stores => StoreNames;

		public string LedgerStore => IapStoreNames.GooglePlay;

		public Task<StoreVerification> VerifyAsync(IapRedeemRequest request, IapServerProduct product, CancellationToken cancellationToken)
		{
			return product.Type == IapProductType.Subscription
				? VerifySubscriptionAsync(request.TransactionId, product, cancellationToken)
				: VerifyProductAsync(request.TransactionId, product, cancellationToken);
		}

		public async Task<bool> AcknowledgeAsync(PurchaseRecord record, IapServerProduct product, CancellationToken cancellationToken)
		{
			var purchasedAt = record.PurchasedAt ?? record.RedeemedAt;

			if (_time.GetUtcNow() - purchasedAt > AcknowledgementWindow)
			{
				_logger.LogError("Gave up acknowledging Google Play {TransactionId} of account {AccountId} purchased at {PurchasedAt}; Google refunds unacknowledged purchases after 3 days",
					record.TransactionId, record.AccountId, purchasedAt);
				return true;
			}

			var subscription = product.Type == IapProductType.Subscription;
			var result = await _api.AcknowledgeAsync(subscription, product.GooglePlayId, record.TransactionId, cancellationToken);

			if (result.Status == GoogleApiStatus.Ok)
			{
				return true;
			}

			if (result.Status == GoogleApiStatus.NotFound)
			{
				// 이미 확인·소비했거나 취소된 결제의 확인은 실패할 수 있다. 상태를 다시 읽어 더 할 것이 있는지 본다
				var done = await IsAcknowledgementSettledAsync(subscription, record.TransactionId, cancellationToken);

				if (done)
				{
					return true;
				}
			}

			// 지급은 끝났다. 클라이언트 확정도 결제를 확인하고, 확인 작업자가 다시 시도한다
			_logger.LogWarning("Could not acknowledge Google Play {TransactionId}; retrying later: {Message}", record.TransactionId, result.Message);
			return false;
		}

		private async Task<bool> IsAcknowledgementSettledAsync(bool subscription, string token, CancellationToken cancellationToken)
		{
			if (subscription)
			{
				var current = await _api.GetSubscriptionAsync(token, cancellationToken);
				return current.Status == GoogleApiStatus.NotFound || current.Status == GoogleApiStatus.Ok &&
					(current.Value.AcknowledgementState == "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED" || current.Value.SubscriptionState == "SUBSCRIPTION_STATE_EXPIRED" ||
					 current.Value.SubscriptionState == "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED");
			}

			var purchase = await _api.GetProductAsync(token, cancellationToken);
			return purchase.Status == GoogleApiStatus.NotFound || purchase.Status == GoogleApiStatus.Ok &&
				(purchase.Value.AcknowledgementState == "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED" || purchase.Value.PurchaseStateContext?.PurchaseState == "CANCELLED");
		}

		private async Task<StoreVerification> VerifyProductAsync(string token, IapServerProduct product, CancellationToken cancellationToken)
		{
			var result = await _api.GetProductAsync(token, cancellationToken);

			if (result.Status != GoogleApiStatus.Ok)
			{
				return Failure(result.Status, result.Message);
			}

			var purchase = result.Value;

			if (!HasProductIds(purchase.ProductLineItem, item => item?.ProductId))
			{
				_logger.LogError("Google Play returned a product purchase without product line items for {Product}", product);
				return StoreVerification.Failed(StoreVerificationStatus.Unavailable, "Purchase has no product line items");
			}

			var lineItem = purchase.ProductLineItem.Find(item => item.ProductId == product.GooglePlayId);

			if (lineItem == null)
			{
				return StoreVerification.Failed(StoreVerificationStatus.ProductMismatch, $"Purchase is not for {product.GooglePlayId}");
			}

			switch (purchase.PurchaseStateContext?.PurchaseState)
			{
				case "PURCHASED":
					break;
				case "PENDING":
					return StoreVerification.Failed(StoreVerificationStatus.Pending, "Payment is pending");
				case "CANCELLED":
					return StoreVerification.Failed(StoreVerificationStatus.Revoked, "Purchase is cancelled");
				default:
					_logger.LogError("Google Play returned unknown purchase state {State} for {Product}", purchase.PurchaseStateContext?.PurchaseState, product);
					return StoreVerification.Failed(StoreVerificationStatus.Unavailable, "Unknown purchase state");
			}

			var orderId = Blank(purchase.OrderId);
			var paid = await ReadOrderTotalAsync(orderId, cancellationToken);
			return StoreVerification.Verified(new VerifiedPurchase
			{
				Store = IapStoreNames.GooglePlay,
				TransactionId = token,
				StoreProductId = product.GooglePlayId,
				ProductType = product.Type,
				OrderId = orderId,
				Quantity = Math.Max(1, lineItem.ProductOfferDetails?.Quantity ?? 1),
				PurchasedAt = ParseTime(purchase.PurchaseCompletionTime),
				ObservedAt = _time.GetUtcNow(),
				IsTest = purchase.TestPurchaseContext != null,
				AccountToken = Blank(purchase.ObfuscatedExternalAccountId),
				PaidAmount = paid.Amount,
				PaidCurrency = paid.Currency,
				RequiresAcknowledgement = RequiresAcknowledgement(purchase.AcknowledgementState)
			});
		}

		private async Task<StoreVerification> VerifySubscriptionAsync(string token, IapServerProduct product, CancellationToken cancellationToken)
		{
			var result = await _api.GetSubscriptionAsync(token, cancellationToken);

			if (result.Status != GoogleApiStatus.Ok)
			{
				return Failure(result.Status, result.Message);
			}

			var subscription = result.Value;

			if (!HasProductIds(subscription.LineItems, item => item?.ProductId))
			{
				_logger.LogError("Google Play returned a subscription without line items for {Product}", product);
				return StoreVerification.Failed(StoreVerificationStatus.Unavailable, "Subscription has no line items");
			}

			var lineItem = subscription.LineItems.Find(item => item.ProductId == product.GooglePlayId);

			if (lineItem == null)
			{
				return StoreVerification.Failed(StoreVerificationStatus.ProductMismatch, $"Subscription is not for {product.GooglePlayId}");
			}

			if (!TryMapSubscriptionState(subscription.SubscriptionState, out var state))
			{
				switch (subscription.SubscriptionState)
				{
					case PendingSubscription:
						return StoreVerification.Failed(StoreVerificationStatus.Pending, "Subscription payment is pending");
					case PendingSubscriptionCanceled:
						return StoreVerification.Failed(StoreVerificationStatus.Revoked, "Pending subscription purchase was cancelled");
					default:
						_logger.LogError("Google Play returned unknown subscription state {State} for {Product}", subscription.SubscriptionState, product);
						return StoreVerification.Failed(StoreVerificationStatus.Unavailable, "Unknown subscription state");
				}
			}

			var orderId = Blank(lineItem.LatestSuccessfulOrderId);
			var paid = await ReadOrderTotalAsync(orderId, cancellationToken);
			return StoreVerification.Verified(new VerifiedPurchase
			{
				Store = IapStoreNames.GooglePlay,
				TransactionId = token,
				StoreProductId = product.GooglePlayId,
				ProductType = product.Type,
				OrderId = orderId,
				PurchasedAt = ParseTime(subscription.StartTime),
				ExpiresAt = ParseTime(lineItem.ExpiryTime),
				SubscriptionState = state,
				AutoRenewing = lineItem.AutoRenewingPlan?.AutoRenewEnabled,
				ObservedAt = _time.GetUtcNow(),
				IsTest = subscription.TestPurchase != null,
				AccountToken = Blank(subscription.ExternalAccountIdentifiers?.ObfuscatedExternalAccountId),
				PaidAmount = paid.Amount,
				PaidCurrency = paid.Currency,
				RequiresAcknowledgement = RequiresAcknowledgement(subscription.AcknowledgementState)
			});
		}

		private async Task<(decimal? Amount, string Currency)> ReadOrderTotalAsync(string orderId, CancellationToken cancellationToken)
		{
			if (!_options.ReadOrderTotal || orderId == null)
			{
				return (null, null);
			}

			var result = await _api.GetOrderAsync(orderId, cancellationToken);
			var total = result.Value?.Total;

			if (result.Status != GoogleApiStatus.Ok || total == null || string.IsNullOrEmpty(total.CurrencyCode) ||
			    !long.TryParse(total.Units ?? "0", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var units))
			{
				_logger.LogWarning("Could not read the total of Google Play order {OrderId}; granting without the paid amount: {Message}", orderId,
					result.Message ?? "no total");
				return (null, null);
			}

			return (units + (total.Nanos ?? 0) / 1_000_000_000m, total.CurrencyCode);
		}

		internal const string PendingSubscription = "SUBSCRIPTION_STATE_PENDING";
		internal const string PendingSubscriptionCanceled = "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED";

		/// <summary>결제가 끝난 구독의 상태를 옮깁니다. 결제 대기·결제 전 취소·모르는 값이면 false 입니다(서버 알림도 씀).</summary>
		internal static bool TryMapSubscriptionState(string value, out IapSubscriptionState state)
		{
			switch (value)
			{
				case "SUBSCRIPTION_STATE_ACTIVE":
					state = IapSubscriptionState.Active;
					return true;
				case "SUBSCRIPTION_STATE_CANCELED":
					state = IapSubscriptionState.Canceled;
					return true;
				case "SUBSCRIPTION_STATE_IN_GRACE_PERIOD":
					state = IapSubscriptionState.GracePeriod;
					return true;
				case "SUBSCRIPTION_STATE_ON_HOLD":
					state = IapSubscriptionState.OnHold;
					return true;
				case "SUBSCRIPTION_STATE_PAUSED":
					state = IapSubscriptionState.Paused;
					return true;
				case "SUBSCRIPTION_STATE_EXPIRED":
					state = IapSubscriptionState.Expired;
					return true;
				default:
					state = default;
					return false;
			}
		}

		/// <summary>확인됐다고 분명히 답한 결제만 빼고 확인 대상입니다(상태가 없거나 모르는 값이면 확인을 시도하고, 이미 확인됐으면 상태를 다시 읽어 끝냄).</summary>
		private bool RequiresAcknowledgement(string state)
		{
			return _options.AcknowledgeAfterGrant && state != "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED";
		}

		/// <summary>목록이 있고 모든 항목에 상품 ID 가 있는지입니다. 아니면 응답을 믿을 수 없어 상품 불일치로 거절하지 않습니다.</summary>
		private static bool HasProductIds<T>(List<T> items, Func<T, string> productId)
		{
			return items != null && items.Count > 0 && items.TrueForAll(item => !string.IsNullOrEmpty(productId(item)));
		}

		private static StoreVerification Failure(GoogleApiStatus status, string message)
		{
			return StoreVerification.Failed(status == GoogleApiStatus.NotFound ? StoreVerificationStatus.Invalid : StoreVerificationStatus.Unavailable, message);
		}

		private static string Blank(string value)
		{
			return string.IsNullOrEmpty(value) ? null : value;
		}

		/// <summary>RFC 3339 시각입니다. Google 은 나노초(소수점 9자리)까지 줄 수 있어 7자리(틱)로 자릅니다. 읽지 못하면 null 입니다.</summary>
		internal static DateTimeOffset? ParseTime(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return null;
			}

			var dot = value.IndexOf('.');

			if (dot >= 0)
			{
				var end = dot + 1;

				while (end < value.Length && char.IsAsciiDigit(value[end]))
				{
					end++;
				}

				if (end - dot - 1 > 7)
				{
					value = value.Substring(0, dot + 8) + value.Substring(end);
				}
			}

			return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
				? time
				: null;
		}
	}
}
