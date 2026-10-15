using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// App Store·Mac App Store 검증기입니다. 요청의 <c>SignedTransaction</c>(StoreKit 2 거래 JWS)을 서명·체인으로 검증하고 번들 ID, 환경, 거래 ID, 상품을 확인합니다.
	/// 환불·철회된 거래(<c>revocationDate</c>)는 지급 없이 닫습니다.
	/// </summary>
	internal sealed class AppStoreVerifier : IStoreVerifier
	{
		internal const string Production = "Production";
		internal const string Sandbox = "Sandbox";

		private static readonly string[] StoreNames = { IapStoreNames.AppleAppStore, IapStoreNames.MacAppStore };

		private readonly AppleSignedDataVerifier _signed;
		private readonly AppStoreOptions _options;
		private readonly TimeProvider _time;

		public AppStoreVerifier(AppleSignedDataVerifier signed, IOptions<AppStoreOptions> options, TimeProvider time)
		{
			_signed = signed;
			_options = options.Value;
			_time = time;
		}

		public IReadOnlyCollection<string> Stores => StoreNames;

		/// <summary>App Store 와 Mac App Store 는 거래 ID 공간이 같다(유니버설 구매)</summary>
		public string LedgerStore => IapStoreNames.AppleAppStore;

		public Task<StoreVerification> VerifyAsync(IapRedeemRequest request, IapServerProduct product, CancellationToken cancellationToken)
		{
			return Task.FromResult(Verify(request, product));
		}

		/// <summary>App Store 는 서버 확인이 없습니다(클라이언트가 거래를 끝냄)</summary>
		public Task<bool> AcknowledgeAsync(PurchaseRecord record, IapServerProduct product, CancellationToken cancellationToken)
		{
			return Task.FromResult(true);
		}

		private StoreVerification Verify(IapRedeemRequest request, IapServerProduct product)
		{
			var signed = _signed.Verify(request.SignedTransaction);

			switch (signed.Status)
			{
				case AppleSignedDataStatus.Valid:
					break;
				case AppleSignedDataStatus.Retryable:
					return StoreVerification.Failed(StoreVerificationStatus.Unavailable, signed.Message);
				default:
					return StoreVerification.Failed(StoreVerificationStatus.Invalid, signed.Message);
			}

			AppleTransactionPayload transaction;

			try
			{
				transaction = JsonSerializer.Deserialize<AppleTransactionPayload>(signed.Payload, IapServerJson.Options);
			}
			catch (JsonException e)
			{
				return StoreVerification.Failed(StoreVerificationStatus.Invalid, $"Unreadable transaction: {e.Message}");
			}

			if (transaction == null)
			{
				return StoreVerification.Failed(StoreVerificationStatus.Invalid, "Empty transaction");
			}

			if (transaction.BundleId != _options.BundleId)
			{
				return StoreVerification.Failed(StoreVerificationStatus.AppMismatch, $"Transaction is for bundle {transaction.BundleId}");
			}

			if (transaction.Environment != Production && (transaction.Environment != Sandbox || !_options.AllowSandbox))
			{
				// Xcode·LocalTesting 거래는 Apple 루트로 서명되지 않아 여기까지 오지 않지만, 루트를 바꿔 넣은 경우를 위해 막는다
				return StoreVerification.Failed(StoreVerificationStatus.EnvironmentNotAllowed, $"Environment {transaction.Environment} is not allowed");
			}

			if (transaction.TransactionId != request.TransactionId)
			{
				return StoreVerification.Failed(StoreVerificationStatus.Invalid, $"Signed transaction is {transaction.TransactionId}");
			}

			if (transaction.ProductId != product.AppStoreId)
			{
				return StoreVerification.Failed(StoreVerificationStatus.ProductMismatch, $"Transaction is for {transaction.ProductId}");
			}

			if (transaction.RevocationDate != null)
			{
				return StoreVerification.Failed(StoreVerificationStatus.Revoked, $"Transaction was revoked (reason {transaction.RevocationReason})");
			}

			var expiresAt = FromMilliseconds(transaction.ExpiresDate);
			IapSubscriptionState? state = null;

			if (product.Type == IapProductType.Subscription)
			{
				// 거래 서명만으로는 자동 갱신 여부를 모른다. 기간 안이면 이용 중으로 본다
				state = expiresAt != null && expiresAt <= _time.GetUtcNow() ? IapSubscriptionState.Expired : IapSubscriptionState.Active;
			}

			var hasPrice = transaction.Price != null && !string.IsNullOrEmpty(transaction.Currency);
			return StoreVerification.Verified(new VerifiedPurchase
			{
				Store = LedgerStore,
				TransactionId = transaction.TransactionId,
				StoreProductId = transaction.ProductId,
				ProductType = product.Type,
				OriginalTransactionId = string.IsNullOrEmpty(transaction.OriginalTransactionId) ? null : transaction.OriginalTransactionId,
				Quantity = Math.Max(1, transaction.Quantity ?? 1),
				PurchasedAt = FromMilliseconds(transaction.PurchaseDate),
				ExpiresAt = expiresAt,
				SubscriptionState = state,
				IsTest = transaction.Environment == Sandbox,
				AccountToken = string.IsNullOrEmpty(transaction.AppAccountToken) ? null : transaction.AppAccountToken,
				PaidAmount = hasPrice ? transaction.Price.Value / 1000m : null,
				PaidCurrency = hasPrice ? transaction.Currency : null
			});
		}

		private static DateTimeOffset? FromMilliseconds(long? value)
		{
			return value == null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);
		}
	}
}
