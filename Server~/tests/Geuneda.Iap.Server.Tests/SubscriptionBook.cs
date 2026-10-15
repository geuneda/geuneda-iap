using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>
	/// README '구독 권한'대로 쓰는 게임 쪽 예시입니다. 알림 처리기(<see cref="BookHandler"/>)가 서버가 합친 스냅숏을 구독 ID 로 그대로 저장하고, 지급자(<see cref="BookGrantor"/>)는
	/// 구독 권한을 쓰지 않습니다.
	/// </summary>
	internal sealed class SubscriptionBook
	{
		private readonly ConcurrentDictionary<(string Store, string SubscriptionId), SubscriptionUpdate> _rows =
			new ConcurrentDictionary<(string, string), SubscriptionUpdate>();

		private Task _holdGrant;

		/// <summary>있으면 다음 지급 하나가 지급하기 전에 이 작업을 기다립니다(지급 검증과 알림의 경합).</summary>
		public Task HoldGrant
		{
			set => Volatile.Write(ref _holdGrant, value);
		}

		public Task TakeHold()
		{
			return Interlocked.Exchange(ref _holdGrant, null);
		}

		public TaskCompletionSource<bool> GrantEntered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		public ConcurrentQueue<SubscriptionUpdate> Deliveries { get; } = new ConcurrentQueue<SubscriptionUpdate>();

		private int _grants;

		/// <summary>지급자가 불린 횟수</summary>
		public int Grants => Volatile.Read(ref _grants);

		public void CountGrant()
		{
			Interlocked.Increment(ref _grants);
		}

		public SubscriptionUpdate Find(string store, string subscriptionId)
		{
			return _rows.TryGetValue((store, subscriptionId), out var row) ? row : null;
		}

		public void Save(SubscriptionUpdate update)
		{
			Deliveries.Enqueue(update);
			_rows[(update.Store, update.SubscriptionId)] = update;
		}
	}

	internal sealed class BookGrantor : IPurchaseGrantor
	{
		private readonly SubscriptionBook _book;

		public BookGrantor(SubscriptionBook book)
		{
			_book = book;
		}

		public async Task<GrantResult> GrantAsync(PurchaseGrant grant, CancellationToken cancellationToken)
		{
			_book.CountGrant();
			var hold = _book.TakeHold();

			if (hold != null)
			{
				_book.GrantEntered.TrySetResult(true);
				await hold;
			}

			return GrantResult.Granted(new TestReward { Product = grant.Product.Id });
		}
	}

	internal sealed class BookHandler : IPurchaseNotificationHandler
	{
		private readonly SubscriptionBook _book;

		public BookHandler(SubscriptionBook book)
		{
			_book = book;
		}

		public Task OnRefundedAsync(PurchaseRefund refund, CancellationToken cancellationToken)
		{
			return Task.CompletedTask;
		}

		public Task OnRefundReversedAsync(PurchaseRefund refund, CancellationToken cancellationToken)
		{
			return Task.CompletedTask;
		}

		public Task OnSubscriptionChangedAsync(SubscriptionUpdate update, CancellationToken cancellationToken)
		{
			_book.Save(update);
			return Task.CompletedTask;
		}
	}
}
