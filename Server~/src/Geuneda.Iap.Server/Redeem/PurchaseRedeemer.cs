using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// <see cref="IPurchaseRedeemer"/> 구현입니다. 순서: 요청 검사 → 원장 확인(지급 기록이 있으면 스토어를 부르지 않음) → 스토어 검증 → 원장 트랜잭션 안에서 지급 → 스토어 확인(Google).
	/// 원장·지급자가 요청 범위일 수 있어 요청 범위로 등록합니다.
	/// </summary>
	internal sealed class PurchaseRedeemer : IPurchaseRedeemer
	{
		private readonly StoreVerifierRegistry _verifiers;
		private readonly IapProductCatalog _catalog;
		private readonly IPurchaseLedger _ledger;
		private readonly IPurchaseGrantor _grantor;
		private readonly SubscriptionSync _subscriptions;
		private readonly IapServerOptions _options;
		private readonly TimeProvider _time;
		private readonly ILogger<PurchaseRedeemer> _logger;

		public PurchaseRedeemer(StoreVerifierRegistry verifiers, IapProductCatalog catalog, IPurchaseLedger ledger, IPurchaseGrantor grantor,
			SubscriptionSync subscriptions, IOptions<IapServerOptions> options, TimeProvider time, ILogger<PurchaseRedeemer> logger)
		{
			_verifiers = verifiers;
			_catalog = catalog;
			_ledger = ledger;
			_grantor = grantor;
			_subscriptions = subscriptions;
			_options = options.Value;
			_time = time;
			_logger = logger;
		}

		public async Task<IapRedeemResponse<object>> RedeemAsync(string accountId, IapRedeemRequest request, CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(accountId))
			{
				throw new ArgumentException("Account id is empty", nameof(accountId));
			}

			if (request == null)
			{
				throw new ArgumentNullException(nameof(request));
			}

			if (string.IsNullOrWhiteSpace(request.Store) || string.IsNullOrWhiteSpace(request.ProductId) || string.IsNullOrWhiteSpace(request.TransactionId))
			{
				return Failure(request, IapRedeemStatus.Rejected, IapRedeemCodes.BadRequest, "store, productId and transactionId are required");
			}

			if (!_verifiers.TryGet(request.Store, out var verifier))
			{
				_logger.LogWarning("Purchase {TransactionId} of account {AccountId} uses unsupported store {Store}", request.TransactionId, accountId, request.Store);
				return Failure(request, IapRedeemStatus.Rejected, IapRedeemCodes.UnsupportedStore, $"Store {request.Store} is not supported");
			}

			if (!_catalog.TryGet(request.ProductId, out var product))
			{
				_logger.LogWarning("Purchase {TransactionId} of account {AccountId} names unknown product {ProductId}", request.TransactionId, accountId, request.ProductId);
				return Failure(request, IapRedeemStatus.Rejected, IapRedeemCodes.ProductMismatch, $"Product {request.ProductId} is not sold");
			}

			// 지급 기록이 있는 거래는 스토어를 부르지 않는다(시간 초과 뒤 재시도, 다른 기기의 같은 계정). 계정 없는 지급 전 환불 기록은 검증한 뒤 지급 트랜잭션에서 본다
			var ledgerStore = verifier.LedgerStore;
			var existing = await _ledger.FindAsync(ledgerStore, request.TransactionId, cancellationToken);

			if (existing != null && existing.IsGranted)
			{
				return FromRecord(existing, accountId);
			}

			var verification = await verifier.VerifyAsync(request, product, cancellationToken)
			                   ?? throw new InvalidOperationException($"{verifier.GetType().Name} returned null");

			if (verification.Status != StoreVerificationStatus.Verified)
			{
				return FromVerification(verification, request, accountId);
			}

			var purchase = verification.Purchase;

			if (purchase.Store != ledgerStore || purchase.TransactionId != request.TransactionId)
			{
				throw new InvalidOperationException($"{verifier.GetType().Name} verified {purchase} for request {ledgerStore} {request.TransactionId}");
			}

			if (purchase.IsTest && !_options.AllowTestPurchases)
			{
				_logger.LogWarning("Rejected test purchase {Purchase} of account {AccountId}", purchase, accountId);
				return Failure(request, IapRedeemStatus.Rejected, IapRedeemCodes.EnvironmentNotAllowed, "Test purchases are not allowed");
			}

			// 서버가 합칠 수 없는 구독 거래(검증기 오용)는 지급자를 부르기 전에 드러낸다
			_subscriptions.EnsureMergeable(purchase);

			var redeemedAt = _time.GetUtcNow();
			var record = new PurchaseRecord
			{
				Store = purchase.Store,
				TransactionId = purchase.TransactionId,
				AccountId = accountId,
				ProductId = product.Id,
				StoreProductId = purchase.StoreProductId,
				OrderId = purchase.OrderId,
				OriginalTransactionId = purchase.OriginalTransactionId,
				Quantity = purchase.Quantity,
				IsTest = purchase.IsTest,
				PurchasedAt = purchase.PurchasedAt,
				RedeemedAt = redeemedAt,
				PaidAmount = purchase.PaidAmount,
				PaidCurrency = purchase.PaidCurrency,
				ExpiresAt = purchase.ExpiresAt,
				AcknowledgementDueAt = purchase.RequiresAcknowledgement ? redeemedAt : null
			};

			var granted = default(GrantResult);
			var refundedBeforeGrant = false;
			var subscriptionUnreadable = false;
			var ledgerResult = await _ledger.RedeemAsync(record, async (preGrantRefund, token) =>
			{
				// 원장이 롤백 뒤 콜백을 다시 부를 수 있으니(동시 요청과의 충돌) 앞 시도의 판정을 남기지 않는다
				granted = default;
				refundedBeforeGrant = false;
				subscriptionUnreadable = false;

				// 지급 요청보다 환불 알림이 먼저 왔으면 그 수량만큼 빼고 지급한다. 모두 환불이면 지급자를 부르지 않고 기록 없이 닫는다
				// (환불 전 영수증을 다시 내도 지급되지 않음. 계정을 기록하면 지급하지 않은 계정이 구독 소유자·환불 취소 수취인이 됨)
				var refunded = preGrantRefund == null ? 0 : Math.Min(preGrantRefund.RefundedQuantity, purchase.Quantity);

				if (refunded >= purchase.Quantity)
				{
					refundedBeforeGrant = true;
					return LedgerGrant.NotGranted;
				}

				granted = await _grantor.GrantAsync(new PurchaseGrant(accountId, product, purchase, request.Payload, purchase.Quantity - refunded), token);

				switch (granted.Status)
				{
					case GrantStatus.Granted:
						// 응답 직렬화를 먼저 한다(구독을 합친 뒤에 실패하지 않게)
						var response = Serialize(granted.Response);

						// 서버 알림을 쓰면 구독 지급을 같은 트랜잭션에서 구독 스냅숏에 합친다(이 계정 소유). 지금 상태를 읽지 못하면 지급까지 롤백하고 다시 받는다
						if (!await _subscriptions.SyncGrantAsync(accountId, purchase, product, token))
						{
							subscriptionUnreadable = true;
							return LedgerGrant.NotGranted;
						}

						return LedgerGrant.Granted(response);
					case GrantStatus.Rejected:
					case GrantStatus.RetryLater:
						return LedgerGrant.NotGranted;
					default:
						throw new InvalidOperationException($"{_grantor.GetType().Name} returned undefined grant status {(int) granted.Status}");
				}
			}, cancellationToken);

			switch (ledgerResult.Outcome)
			{
				case LedgerRedeemOutcome.Duplicate:
					// 같은 거래의 다른 요청이 먼저 지급했다
					return FromRecord(ledgerResult.Record, accountId);
				case LedgerRedeemOutcome.NotGranted when refundedBeforeGrant:
					LogClosedWithoutGrant(purchase.Store, purchase.TransactionId, product.Id, accountId, "refunded before the grant");
					return Response(record, IapRedeemStatus.AlreadyGranted, IapRedeemCodes.Revoked, "Refunded before the grant", null);
				case LedgerRedeemOutcome.NotGranted when subscriptionUnreadable:
					_logger.LogWarning("Could not read the subscription state of {Purchase} for account {AccountId}; rolled the grant back", purchase, accountId);
					return Failure(request, IapRedeemStatus.RetryLater, IapRedeemCodes.StoreUnavailable, "Could not read the subscription state");
				case LedgerRedeemOutcome.NotGranted:
					return FromNotGranted(granted, request, accountId);
				case LedgerRedeemOutcome.Recorded:
					_logger.LogInformation("Granted {Purchase} to account {AccountId}", purchase, accountId);

					// 지급은 커밋됐으니 요청이 끊겨도 스토어 확인은 끝까지 한다. 실패하면 기록에 남은 대기 표시로 확인 작업자가 다시 시도한다
					if (ledgerResult.Record.AcknowledgementDueAt != null && await verifier.AcknowledgeAsync(ledgerResult.Record, product, CancellationToken.None))
					{
						await _ledger.MarkAcknowledgedAsync(ledgerResult.Record.Store, ledgerResult.Record.TransactionId, CancellationToken.None);
					}

					return Response(ledgerResult.Record, IapRedeemStatus.Granted, IapRedeemCodes.None, null, granted.Response);
				default:
					throw new InvalidOperationException($"{_ledger.GetType().Name} returned undefined outcome {(int) ledgerResult.Outcome}");
			}
		}

		private IapRedeemResponse<object> FromRecord(PurchaseRecord record, string accountId)
		{
			if (record.IsFullyRefunded)
			{
				// 지급 뒤 환불된 거래가 다시 왔다(클라이언트가 아직 닫지 못함). 다시 지급하지 않고 닫게 한다
				_logger.LogInformation("{Store} transaction {TransactionId} of account {AccountId} was refunded after the grant", record.Store, record.TransactionId,
					accountId);
				return Response(record, IapRedeemStatus.AlreadyGranted, IapRedeemCodes.Revoked, "Refunded", null);
			}

			if (record.AccountId != accountId)
			{
				// 레퍼런스의 '남의 영수증'과 같이 지급하지 않고 거래를 닫게 한다(같은 거래가 계속 다시 오지 않게)
				_logger.LogWarning("Account {AccountId} sent {Store} transaction {TransactionId} already granted to account {OwnerId}",
					accountId, record.Store, record.TransactionId, record.AccountId);
				return Response(record, IapRedeemStatus.AlreadyGranted, IapRedeemCodes.OwnedByOtherAccount, "Granted to another account", null);
			}

			var grant = record.GrantResponse == null ? null : (object) JsonSerializer.Deserialize<JsonElement>(record.GrantResponse);
			return Response(record, IapRedeemStatus.AlreadyGranted, IapRedeemCodes.Duplicate, "Already granted", grant);
		}

		private IapRedeemResponse<object> FromVerification(StoreVerification verification, IapRedeemRequest request, string accountId)
		{
			switch (verification.Status)
			{
				case StoreVerificationStatus.Invalid:
				case StoreVerificationStatus.AppMismatch:
				case StoreVerificationStatus.ProductMismatch:
				case StoreVerificationStatus.EnvironmentNotAllowed:
					_logger.LogWarning("Rejected {Store} transaction {TransactionId} of account {AccountId}: {Verification}",
						request.Store, request.TransactionId, accountId, verification);
					return Failure(request, IapRedeemStatus.Rejected, RejectCode(verification.Status), verification.Message);
				case StoreVerificationStatus.Revoked:
					LogClosedWithoutGrant(request.Store, request.TransactionId, request.ProductId, accountId, verification.Message);
					return Failure(request, IapRedeemStatus.AlreadyGranted, IapRedeemCodes.Revoked, verification.Message);
				case StoreVerificationStatus.Pending:
					return Failure(request, IapRedeemStatus.RetryLater, IapRedeemCodes.PaymentPending, verification.Message);
				case StoreVerificationStatus.Unavailable:
					return Failure(request, IapRedeemStatus.RetryLater, IapRedeemCodes.StoreUnavailable, verification.Message);
				default:
					throw new InvalidOperationException($"Undefined verification status {(int) verification.Status}");
			}
		}

		private IapRedeemResponse<object> FromNotGranted(GrantResult granted, IapRedeemRequest request, string accountId)
		{
			switch (granted.Status)
			{
				case GrantStatus.Rejected:
					_logger.LogWarning("Grantor rejected {Store} transaction {TransactionId} of account {AccountId}: {Grant}",
						request.Store, request.TransactionId, accountId, granted);
					return Failure(request, IapRedeemStatus.Rejected, IapRedeemCodes.GrantRejected, granted.Message);
				case GrantStatus.RetryLater:
					return Failure(request, IapRedeemStatus.RetryLater, IapRedeemCodes.GrantDeferred, granted.Message);
				default:
					throw new InvalidOperationException($"{_ledger.GetType().Name} returned NotGranted without calling the grantor");
			}
		}

		/// <summary>
		/// 지급 없이 닫는 거래를 계정과 함께 남깁니다(Warning). 원장에는 계정을 기록하지 않으므로, App Store 가 나중에 환불을 취소하면 고객 지원이 이 로그로 지급할 계정을 찾습니다.
		/// </summary>
		private void LogClosedWithoutGrant(string store, string transactionId, string productId, string accountId, string reason)
		{
			_logger.LogWarning("Closed {Store} transaction {TransactionId} ({ProductId}) of account {AccountId} without granting: {Reason}", store, transactionId,
				productId, accountId, reason);
		}

		private static int RejectCode(StoreVerificationStatus status)
		{
			switch (status)
			{
				case StoreVerificationStatus.AppMismatch:
					return IapRedeemCodes.AppMismatch;
				case StoreVerificationStatus.ProductMismatch:
					return IapRedeemCodes.ProductMismatch;
				case StoreVerificationStatus.EnvironmentNotAllowed:
					return IapRedeemCodes.EnvironmentNotAllowed;
				default:
					return IapRedeemCodes.InvalidReceipt;
			}
		}

		private static string Serialize(object response)
		{
			return response == null ? null : JsonSerializer.Serialize(response, response.GetType(), IapServerJson.Options);
		}

		private static IapRedeemResponse<object> Response(PurchaseRecord record, IapRedeemStatus status, int code, string message, object grant)
		{
			return new IapRedeemResponse<object>
			{
				Status = status,
				Code = code,
				Message = message,
				Grant = grant,
				ProductId = record.ProductId,
				TransactionId = record.TransactionId,
				Quantity = record.Quantity,
				IsTest = record.IsTest,
				PaidAmount = record.PaidAmount,
				PaidCurrency = record.PaidCurrency,
				ExpiresAt = record.ExpiresAt
			};
		}

		private static IapRedeemResponse<object> Failure(IapRedeemRequest request, IapRedeemStatus status, int code, string message)
		{
			return new IapRedeemResponse<object>
			{
				Status = status,
				Code = code,
				Message = message,
				ProductId = request.ProductId,
				TransactionId = request.TransactionId,
				Quantity = 1
			};
		}
	}
}
