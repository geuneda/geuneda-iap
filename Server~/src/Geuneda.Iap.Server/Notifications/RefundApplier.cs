using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 알림의 환불 상태를 원장에 반영하고, 누적 환불 수량이 바뀐 만큼 알림 처리기를 원장 트랜잭션 안에서 부릅니다(늘면 회수, 줄면 다시 지급).
	/// 지급 전의 거래면 원장이 계정 없는 환불 기록으로 남기고 처리기를 부르지 않습니다. 원장·처리기와 같은 요청 범위입니다.
	/// </summary>
	internal sealed class RefundApplier
	{
		private readonly IPurchaseLedger _ledger;
		private readonly IPurchaseNotificationHandler _handler;
		private readonly IapProductCatalog _catalog;
		private readonly ILogger<RefundApplier> _logger;

		public RefundApplier(IPurchaseLedger ledger, IPurchaseNotificationHandler handler, IapProductCatalog catalog, ILogger<RefundApplier> logger)
		{
			_ledger = ledger;
			_handler = handler;
			_catalog = catalog;
			_logger = logger;
		}

		public async Task<LedgerRefundOutcome> ApplyAsync(RefundChange change, string reason, CancellationToken cancellationToken)
		{
			_catalog.TryGet(change.ProductId, out var product);
			var result = await _ledger.UpdateRefundAsync(change, (current, delta, token) =>
			{
				var target = current.RefundedQuantity + delta;
				var refund = new PurchaseRefund(current, product, Math.Abs(delta), target, reason, change.ChangedAt);

				if (delta > 0)
				{
					_logger.LogInformation("Revoking {Refund} from account {AccountId}", refund, current.AccountId);
					return _handler.OnRefundedAsync(refund, token);
				}

				_logger.LogInformation("Restoring {Refund} to account {AccountId}", refund, current.AccountId);
				return _handler.OnRefundReversedAsync(refund, token);
			}, cancellationToken);

			switch (result.Outcome)
			{
				case LedgerRefundOutcome.Stale:
					_logger.LogInformation("Ignored an older refund change for {Change} ({Reason})", change, reason);
					break;
				case LedgerRefundOutcome.RecordedBeforeGrant:
					// 지급 요청이 오면 이 수량만큼 빼고 지급하거나 닫는다(환불 전 영수증을 다시 내도 지급되지 않음)
					_logger.LogInformation("Recorded {Change} before its grant ({Reason})", change, reason);
					break;
			}

			return result.Outcome;
		}
	}
}
