using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 메모리 원장입니다. 개발·테스트용입니다. 서버를 다시 시작하면 기록이 사라지고, 게임 데이터가 메모리 밖(DB)에 있으면 원장과 지급이 한 트랜잭션이 아닙니다.
	/// 같은 거래의 동시 요청은 거래마다 하나씩 처리합니다.
	/// </summary>
	public sealed class InMemoryPurchaseLedger : IPurchaseLedger
	{
		private readonly ConcurrentDictionary<(string Store, string TransactionId), PurchaseRecord> _records =
			new ConcurrentDictionary<(string Store, string TransactionId), PurchaseRecord>();

		private readonly ConcurrentDictionary<(string Store, string TransactionId), SemaphoreSlim> _gates =
			new ConcurrentDictionary<(string Store, string TransactionId), SemaphoreSlim>();

		private readonly ConcurrentDictionary<(string Store, string SubscriptionId), SubscriptionUpdate> _subscriptions =
			new ConcurrentDictionary<(string Store, string SubscriptionId), SubscriptionUpdate>();

		// 구독 잠금은 거래 잠금과 따로 둔다(구독 전달 안에서 같은 키의 거래를 고쳐도 교착하지 않게)
		private readonly ConcurrentDictionary<(string Store, string SubscriptionId), SemaphoreSlim> _subscriptionGates =
			new ConcurrentDictionary<(string Store, string SubscriptionId), SemaphoreSlim>();

		public Task<PurchaseRecord> FindAsync(string store, string transactionId, CancellationToken cancellationToken)
		{
			_records.TryGetValue((store, transactionId), out var record);
			return Task.FromResult(record);
		}

		public async Task<LedgerRedeemResult> RedeemAsync(PurchaseRecord record, Func<PurchaseRecord, CancellationToken, Task<LedgerGrant>> grant,
			CancellationToken cancellationToken)
		{
			if (record == null)
			{
				throw new ArgumentNullException(nameof(record));
			}

			if (grant == null)
			{
				throw new ArgumentNullException(nameof(grant));
			}

			var key = (record.Store, record.TransactionId);
			var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
			await gate.WaitAsync(cancellationToken);

			try
			{
				_records.TryGetValue(key, out var existing);

				if (existing != null && existing.IsGranted)
				{
					return LedgerRedeemResult.Duplicate(existing);
				}

				var granted = await grant(existing, cancellationToken);

				if (!granted.IsGranted)
				{
					return LedgerRedeemResult.NotGranted();
				}

				// 지급 전 환불 기록이 있었으면 그 환불 수량·시각을 이어받는다(이후 환불 알림의 늘어난 수량 계산)
				var stored = record with
				{
					GrantResponse = granted.Response,
					RefundedQuantity = existing == null ? record.RefundedQuantity : Math.Min(existing.RefundedQuantity, Math.Max(1, record.Quantity)),
					RefundedAt = existing?.RefundedAt ?? record.RefundedAt
				};
				_records[key] = stored;
				return LedgerRedeemResult.Recorded(stored);
			}
			finally
			{
				gate.Release();
			}
		}

		public Task<IReadOnlyList<PurchaseRecord>> FindDueAcknowledgementsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
		{
			IReadOnlyList<PurchaseRecord> due = _records.Values.Where(record => record.AcknowledgementDueAt <= now).OrderBy(record => record.AcknowledgementDueAt)
				.Take(limit).ToList();
			return Task.FromResult(due);
		}

		public Task MarkAcknowledgedAsync(string store, string transactionId, CancellationToken cancellationToken)
		{
			return UpdateAsync(store, transactionId, record => record with { AcknowledgementDueAt = null }, cancellationToken);
		}

		public Task DeferAcknowledgementAsync(string store, string transactionId, DateTimeOffset dueAt, CancellationToken cancellationToken)
		{
			return UpdateAsync(store, transactionId, record => record.AcknowledgementDueAt == null ? record : record with { AcknowledgementDueAt = dueAt },
				cancellationToken);
		}

		public Task<PurchaseRecord> FindByOriginalTransactionAsync(string store, string originalTransactionId, CancellationToken cancellationToken)
		{
			var record = _records.Values
				.Where(item => item.IsGranted && item.Store == store &&
				               (item.OriginalTransactionId == originalTransactionId || item.TransactionId == originalTransactionId))
				.OrderByDescending(item => item.RedeemedAt).FirstOrDefault();
			return Task.FromResult(record);
		}

		public async Task<LedgerRefundResult> UpdateRefundAsync(RefundChange change, Func<PurchaseRecord, int, CancellationToken, Task> apply,
			CancellationToken cancellationToken)
		{
			if (change == null)
			{
				throw new ArgumentNullException(nameof(change));
			}

			if (apply == null)
			{
				throw new ArgumentNullException(nameof(apply));
			}

			var key = (change.Store, change.TransactionId);
			var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
			await gate.WaitAsync(cancellationToken);

			try
			{
				var refundedQuantity = change.RefundedQuantity;
				var changedAt = change.ChangedAt;

				if (!_records.TryGetValue(key, out var record))
				{
					// 지급 요청보다 환불 알림이 먼저 왔다. 나중의 지급이 이 기록을 보고 환불 수량만큼 빼고 지급하거나 닫는다
					var quantity = Math.Max(1, change.Quantity);
					var placeholder = new PurchaseRecord
					{
						Store = change.Store,
						TransactionId = change.TransactionId,
						AccountId = null,
						ProductId = change.ProductId,
						StoreProductId = change.StoreProductId,
						Quantity = quantity,
						RefundedQuantity = Math.Clamp(refundedQuantity, 0, quantity),
						RefundedAt = changedAt
					};
					_records[key] = placeholder;
					return LedgerRefundResult.RecordedBeforeGrant(placeholder);
				}

				var mode = change.Mode;
				var ordered = mode == RefundUpdateMode.EventOrdered;

				if (ordered && record.RefundedAt != null && changedAt < record.RefundedAt)
				{
					return LedgerRefundResult.Stale(record);
				}

				var target = Math.Clamp(refundedQuantity, 0, Math.Max(1, record.Quantity));
				target = ordered ? target : Math.Max(target, record.RefundedQuantity);
				var at = ordered || record.RefundedAt == null || changedAt > record.RefundedAt ? changedAt : record.RefundedAt.Value;
				var delta = target - record.RefundedQuantity;

				if (!record.IsGranted)
				{
					var updated = record with { RefundedQuantity = target, RefundedAt = at };
					_records[key] = updated;
					return LedgerRefundResult.RecordedBeforeGrant(updated);
				}

				if (delta == 0)
				{
					var advanced = record with { RefundedAt = at };
					_records[key] = advanced;
					return LedgerRefundResult.Unchanged(advanced);
				}

				await apply(record, delta, cancellationToken);
				var stored = record with { RefundedQuantity = target, RefundedAt = at };
				_records[key] = stored;
				return LedgerRefundResult.Changed(stored);
			}
			finally
			{
				gate.Release();
			}
		}

		public async Task<SubscriptionUpdateOutcome> UpdateSubscriptionAsync(string store, string subscriptionId,
			Func<SubscriptionUpdate, CancellationToken, Task<SubscriptionUpdate>> next, Func<SubscriptionUpdate, CancellationToken, Task> deliver,
			CancellationToken cancellationToken)
		{
			if (next == null)
			{
				throw new ArgumentNullException(nameof(next));
			}

			if (deliver == null)
			{
				throw new ArgumentNullException(nameof(deliver));
			}

			var key = (store, subscriptionId);
			var gate = _subscriptionGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
			await gate.WaitAsync(cancellationToken);

			try
			{
				_subscriptions.TryGetValue(key, out var stored);
				var candidate = await next(stored, cancellationToken);

				if (candidate == null)
				{
					return SubscriptionUpdateOutcome.Skipped;
				}

				if (stored != null && candidate.ObservedAt < stored.ObservedAt)
				{
					return SubscriptionUpdateOutcome.Stale;
				}

				await deliver(candidate, cancellationToken);
				_subscriptions[key] = candidate;
				return SubscriptionUpdateOutcome.Delivered;
			}
			finally
			{
				gate.Release();
			}
		}

		private async Task UpdateAsync(string store, string transactionId, Func<PurchaseRecord, PurchaseRecord> update, CancellationToken cancellationToken)
		{
			var key = (store, transactionId);
			var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
			await gate.WaitAsync(cancellationToken);

			try
			{
				if (_records.TryGetValue(key, out var record))
				{
					_records[key] = update(record);
				}
			}
			finally
			{
				gate.Release();
			}
		}
	}
}
