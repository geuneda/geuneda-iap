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

		public Task<PurchaseRecord> FindAsync(string store, string transactionId, CancellationToken cancellationToken)
		{
			_records.TryGetValue((store, transactionId), out var record);
			return Task.FromResult(record);
		}

		public async Task<LedgerRedeemResult> RedeemAsync(PurchaseRecord record, Func<CancellationToken, Task<LedgerGrant>> grant,
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
				if (_records.TryGetValue(key, out var existing))
				{
					return LedgerRedeemResult.Duplicate(existing);
				}

				var granted = await grant(cancellationToken);

				if (!granted.IsGranted)
				{
					return LedgerRedeemResult.NotGranted();
				}

				var stored = record with { GrantResponse = granted.Response };
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
