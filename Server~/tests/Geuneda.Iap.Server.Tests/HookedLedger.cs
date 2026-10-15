using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>
	/// 메모리 원장 앞에 끼어 DB 원장에서만 생기는 순서를 흉내 냅니다. 지급 콜백의 롤백 뒤 재시도(<see cref="FirstAttemptPreGrant"/>)와
	/// 구독 잠금에 들어가기 전의 지연(<see cref="HoldFirstSubscriptionUpdate"/>)입니다.
	/// </summary>
	internal sealed class HookedLedger : IPurchaseLedger
	{
		private readonly InMemoryPurchaseLedger _inner;
		private int _subscriptionUpdates;

		public HookedLedger(InMemoryPurchaseLedger inner)
		{
			_inner = inner;
		}

		/// <summary>있으면 다음 지급에서 콜백을 이 지급 전 환불 기록으로 한 번 먼저 부르고 결과를 버립니다(커밋 충돌로 롤백한 첫 시도).</summary>
		public PurchaseRecord FirstAttemptPreGrant
		{
			get => Volatile.Read(ref _firstAttemptPreGrant);
			set => Volatile.Write(ref _firstAttemptPreGrant, value);
		}

		/// <summary>있으면 첫 구독 갱신이 구독 잠금에 들어가기 전에 이 작업을 기다립니다.</summary>
		public Task HoldFirstSubscriptionUpdate { get; set; }

		public TaskCompletionSource<bool> FirstSubscriptionUpdateEntered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<PurchaseRecord> FindAsync(string store, string transactionId, CancellationToken cancellationToken)
		{
			return _inner.FindAsync(store, transactionId, cancellationToken);
		}

		public async Task<LedgerRedeemResult> RedeemAsync(PurchaseRecord record, Func<PurchaseRecord, CancellationToken, Task<LedgerGrant>> grant,
			CancellationToken cancellationToken)
		{
			var firstAttempt = Interlocked.Exchange(ref _firstAttemptPreGrant, null);

			if (firstAttempt != null)
			{
				await grant(firstAttempt, cancellationToken);
			}

			return await _inner.RedeemAsync(record, grant, cancellationToken);
		}

		public Task<IReadOnlyList<PurchaseRecord>> FindDueAcknowledgementsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
		{
			return _inner.FindDueAcknowledgementsAsync(now, limit, cancellationToken);
		}

		public Task MarkAcknowledgedAsync(string store, string transactionId, CancellationToken cancellationToken)
		{
			return _inner.MarkAcknowledgedAsync(store, transactionId, cancellationToken);
		}

		public Task DeferAcknowledgementAsync(string store, string transactionId, DateTimeOffset dueAt, CancellationToken cancellationToken)
		{
			return _inner.DeferAcknowledgementAsync(store, transactionId, dueAt, cancellationToken);
		}

		public Task<LedgerRefundResult> UpdateRefundAsync(RefundChange change, Func<PurchaseRecord, int, CancellationToken, Task> apply,
			CancellationToken cancellationToken)
		{
			return _inner.UpdateRefundAsync(change, apply, cancellationToken);
		}

		public Task<PurchaseRecord> FindByOriginalTransactionAsync(string store, string originalTransactionId, CancellationToken cancellationToken)
		{
			return _inner.FindByOriginalTransactionAsync(store, originalTransactionId, cancellationToken);
		}

		public async Task<SubscriptionUpdateOutcome> UpdateSubscriptionAsync(string store, string subscriptionId,
			Func<SubscriptionUpdate, CancellationToken, Task<SubscriptionUpdate>> next, Func<SubscriptionUpdate, CancellationToken, Task> deliver,
			CancellationToken cancellationToken)
		{
			if (Interlocked.Increment(ref _subscriptionUpdates) == 1 && HoldFirstSubscriptionUpdate != null)
			{
				FirstSubscriptionUpdateEntered.TrySetResult(true);
				await HoldFirstSubscriptionUpdate;
			}

			return await _inner.UpdateSubscriptionAsync(store, subscriptionId, next, deliver, cancellationToken);
		}

		private PurchaseRecord _firstAttemptPreGrant;
	}
}
