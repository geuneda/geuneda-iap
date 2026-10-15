using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>받은 알림을 기록하는 처리기입니다. <see cref="ThrowOnce"/> 를 부르면 다음 호출 한 번에 던집니다.</summary>
	internal sealed class TestNotificationHandler : IPurchaseNotificationHandler
	{
		public ConcurrentQueue<PurchaseRefund> Refunds { get; } = new ConcurrentQueue<PurchaseRefund>();
		public ConcurrentQueue<PurchaseRefund> Reversals { get; } = new ConcurrentQueue<PurchaseRefund>();
		public ConcurrentQueue<SubscriptionUpdate> Subscriptions { get; } = new ConcurrentQueue<SubscriptionUpdate>();

		public Task OnRefundedAsync(PurchaseRefund refund, CancellationToken cancellationToken)
		{
			return Record(Refunds, refund);
		}

		public Task OnRefundReversedAsync(PurchaseRefund refund, CancellationToken cancellationToken)
		{
			return Record(Reversals, refund);
		}

		public Task OnSubscriptionChangedAsync(SubscriptionUpdate update, CancellationToken cancellationToken)
		{
			return Record(Subscriptions, update);
		}

		private Task Record<T>(ConcurrentQueue<T> queue, T value)
		{
			var error = Interlocked.Exchange(ref _throw, null);

			if (error != null)
			{
				return Task.FromException(error);
			}

			queue.Enqueue(value);
			return Task.CompletedTask;
		}

		private Exception _throw;

		public void ThrowOnce(Exception error)
		{
			_throw = error;
		}
	}
}
