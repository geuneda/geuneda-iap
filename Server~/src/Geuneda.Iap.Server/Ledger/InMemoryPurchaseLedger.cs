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
	/// 같은 거래의 동시 요청은 거래마다 하나씩 처리합니다. 지급 콜백 안의 구독 갱신(구독 지급)은 지급과 함께 저장하고, 그동안 그 구독 잠금을 쥡니다(지급이 실패하면 버림).
	/// </summary>
	public sealed class InMemoryPurchaseLedger : IPurchaseLedger
	{
		private readonly ConcurrentDictionary<(string Store, string TransactionId), PurchaseRecord> _records =
			new ConcurrentDictionary<(string Store, string TransactionId), PurchaseRecord>();

		private readonly ConcurrentDictionary<(string Store, string TransactionId), SemaphoreSlim> _gates =
			new ConcurrentDictionary<(string Store, string TransactionId), SemaphoreSlim>();

		private readonly ConcurrentDictionary<(string Store, string SubscriptionId), SubscriptionUpdate> _subscriptions =
			new ConcurrentDictionary<(string Store, string SubscriptionId), SubscriptionUpdate>();

		// 구독 잠금은 거래 잠금과 따로 둔다. 구독 지급은 거래 잠금 안에서 구독 잠금을 잡고(순서는 늘 거래 → 구독), 구독 잠금 안에서는 거래 잠금을 잡지 않는다
		private readonly ConcurrentDictionary<(string Store, string SubscriptionId), SemaphoreSlim> _subscriptionGates =
			new ConcurrentDictionary<(string Store, string SubscriptionId), SemaphoreSlim>();

		// 지금 흐름이 지급 콜백 안이면 그 지급의 트랜잭션(구독 갱신을 지급과 함께 반영)
		private readonly AsyncLocal<GrantTransaction> _grant = new AsyncLocal<GrantTransaction>();

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
			var transaction = new GrantTransaction();
			_grant.Value = transaction;

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

				foreach (var subscription in transaction.Complete())
				{
					_subscriptions[subscription.Key] = subscription.Value;
				}

				return LedgerRedeemResult.Recorded(stored);
			}
			finally
			{
				_grant.Value = null;

				foreach (var held in transaction.Close())
				{
					held.Release();
				}

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
			var transaction = _grant.Value;

			if (transaction != null)
			{
				return await UpdateInGrantAsync(transaction, key, gate, next, deliver, cancellationToken);
			}

			await gate.WaitAsync(cancellationToken);

			try
			{
				_subscriptions.TryGetValue(key, out var stored);
				var candidate = await next(stored, cancellationToken);

				if (candidate == null)
				{
					return SubscriptionUpdateOutcome.Skipped;
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

		/// <summary>
		/// 지급 콜백 안의 구독 갱신입니다. 구독 잠금은 지급이 끝날 때 풀고, 스냅숏은 지급을 기록할 때 저장합니다. 지급이 끝난 뒤에 불리면(콜백이 기다리지 않은 작업) 예외입니다.
		/// </summary>
		private async Task<SubscriptionUpdateOutcome> UpdateInGrantAsync(GrantTransaction transaction, (string, string) key, SemaphoreSlim gate,
			Func<SubscriptionUpdate, CancellationToken, Task<SubscriptionUpdate>> next, Func<SubscriptionUpdate, CancellationToken, Task> deliver,
			CancellationToken cancellationToken)
		{
			// 같은 지급 안의 갱신은 하나씩 한다(동시에 들어와 둘 다 구독 잠금을 기다리면 먼저 잡은 쪽의 잠금이 지급 끝까지 풀리지 않아 교착)
			await transaction.Serial.WaitAsync(cancellationToken);

			try
			{
				if (!transaction.Holds(gate))
				{
					await gate.WaitAsync(cancellationToken);
					transaction.Hold(gate);
				}

				if (!transaction.TryGetStaged(key, out var stored))
				{
					_subscriptions.TryGetValue(key, out stored);
				}

				var candidate = await next(stored, cancellationToken);

				if (candidate == null)
				{
					return SubscriptionUpdateOutcome.Skipped;
				}

				await deliver(candidate, cancellationToken);
				transaction.Stage(key, candidate);
				return SubscriptionUpdateOutcome.Delivered;
			}
			finally
			{
				transaction.Serial.Release();
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

		/// <summary>
		/// 지급 하나가 쥔 구독 잠금과 함께 저장할 구독 스냅숏입니다. 지급이 끝나면 닫혀, 콜백이 기다리지 않은 작업이 그 뒤에 쓰거나 잠그려 하면 예외입니다(잠금이 새거나 저장이 사라지지 않게).
		/// </summary>
		private sealed class GrantTransaction
		{
			private readonly object _sync = new object();
			private readonly List<SemaphoreSlim> _gates = new List<SemaphoreSlim>();
			private readonly Dictionary<(string Store, string SubscriptionId), SubscriptionUpdate> _staged = new Dictionary<(string, string), SubscriptionUpdate>();
			private bool _closed;

			/// <summary>이 지급 안의 구독 갱신을 하나씩 하게 합니다</summary>
			public SemaphoreSlim Serial { get; } = new SemaphoreSlim(1, 1);

			public bool Holds(SemaphoreSlim gate)
			{
				lock (_sync)
				{
					ThrowIfClosed();
					return _gates.Contains(gate);
				}
			}

			/// <summary>잡은 구독 잠금을 맡깁니다. 그사이 지급이 끝났으면 잠금을 풀고 예외입니다.</summary>
			public void Hold(SemaphoreSlim gate)
			{
				lock (_sync)
				{
					if (!_closed)
					{
						_gates.Add(gate);
						return;
					}
				}

				gate.Release();
				ThrowClosed();
			}

			public bool TryGetStaged((string, string) key, out SubscriptionUpdate update)
			{
				lock (_sync)
				{
					ThrowIfClosed();
					return _staged.TryGetValue(key, out update);
				}
			}

			public void Stage((string, string) key, SubscriptionUpdate update)
			{
				lock (_sync)
				{
					ThrowIfClosed();
					_staged[key] = update;
				}
			}

			/// <summary>지급을 기록할 때 저장할 스냅숏을 돌려주고 닫습니다.</summary>
			public IReadOnlyList<KeyValuePair<(string Store, string SubscriptionId), SubscriptionUpdate>> Complete()
			{
				lock (_sync)
				{
					_closed = true;
					return _staged.ToList();
				}
			}

			/// <summary>닫고, 쥔 구독 잠금을 돌려줍니다(지급이 실패했으면 보류한 스냅숏은 버림).</summary>
			public IReadOnlyList<SemaphoreSlim> Close()
			{
				lock (_sync)
				{
					_closed = true;
					var gates = _gates.ToList();
					_gates.Clear();
					return gates;
				}
			}

			private void ThrowIfClosed()
			{
				if (_closed)
				{
					ThrowClosed();
				}
			}

			private static void ThrowClosed()
			{
				throw new InvalidOperationException("A subscription update ran after its grant finished; await ledger calls inside the grant callback");
			}
		}
	}
}
