using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Tests
{
	public sealed class FakeMonotonicClock : IMonotonicClock
	{
		public TimeSpan Elapsed { get; set; } = TimeSpan.FromHours(1);
	}

	/// <summary>대기를 기록하고 테스트가 끝낼 때까지 기다리게 하는 지연입니다. 취소되면 바로 끝납니다.</summary>
	public sealed class ManualDelay
	{
		private readonly List<(TimeSpan Delay, UniTaskCompletionSource Source)> _pending = new List<(TimeSpan, UniTaskCompletionSource)>();

		public List<TimeSpan> Requested { get; } = new List<TimeSpan>();

		public UniTask Delay(TimeSpan delay, CancellationToken cancellationToken)
		{
			Requested.Add(delay);
			var source = new UniTaskCompletionSource();
			cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
			_pending.Add((delay, source));
			return source.Task;
		}

		public int PendingCount(TimeSpan delay)
		{
			Prune();
			var count = 0;

			foreach (var entry in _pending)
			{
				if (entry.Delay == delay)
				{
					count++;
				}
			}

			return count;
		}

		/// <summary>이 길이의 지연을 모두 끝냅니다. 끝내는 동안 새로 생긴 지연은 그대로 둡니다.</summary>
		public int Complete(TimeSpan delay)
		{
			Prune();
			var matched = _pending.FindAll(entry => entry.Delay == delay);
			_pending.RemoveAll(entry => entry.Delay == delay);

			foreach (var entry in matched)
			{
				entry.Source.TrySetResult();
			}

			return matched.Count;
		}

		private void Prune()
		{
			_pending.RemoveAll(entry => entry.Source.Task.Status != UniTaskStatus.Pending);
		}
	}

	/// <summary>스토어 SDK 대신 쓰는 가짜 스토어입니다. 스토어 상태(미확정·보유 주문)를 들고, 확정하면 미확정 목록에서 뺍니다.</summary>
	public sealed class FakeStore : IIapStore
	{
		private int _nextTransaction = 1;

		public string Name => "FakeStore";

		public bool IsConnected { get; set; } = true;

		public int ConnectCalls { get; private set; }

		public Func<UniTask<Result>> ConnectHandler { get; set; }

		public HashSet<string> MissingProducts { get; } = new HashSet<string>();

		public HashSet<string> UnavailableProducts { get; } = new HashSet<string>();

		public Func<UniTask<Result<IReadOnlyList<IapProduct>>>> ProductsHandler { get; set; }

		public int FetchProductsCalls { get; private set; }

		public List<IapStoreOrder> Pending { get; } = new List<IapStoreOrder>();

		public List<IapStoreOrder> Owned { get; } = new List<IapStoreOrder>();

		public int FetchOrdersCalls { get; private set; }

		/// <summary>주문 조회 순서 기록(상품 조회 뒤에 했는지 확인)</summary>
		public List<string> Calls { get; } = new List<string>();

		public Func<UniTask<Result<IapStoreOrders>>> OrdersHandler { get; set; }

		public List<IapProductDefinition> Purchases { get; } = new List<IapProductDefinition>();

		public Action<IapProductDefinition> OnPurchase { get; set; }

		public List<string> ConfirmCalls { get; } = new List<string>();

		public List<int> ConfirmThreads { get; } = new List<int>();

		public List<object> ConfirmNatives { get; } = new List<object>();

		public Func<IapStoreOrder, UniTask<IapConfirmResult>> ConfirmHandler { get; set; }

		public int RestoreCalls { get; private set; }

		public Func<UniTask<Result>> RestoreHandler { get; set; }

		public Func<IapProductDefinition, UniTask<Result<IapEntitlementStatus>>> EntitlementHandler { get; set; }

		public event Action<IapStoreOrder> OrderPending;

		public event Action<IapStorePurchaseFailure> PurchaseFailed;

		public event Action<string> PurchaseDeferred;

		public event Action<string> Disconnected;

		public bool HasSubscribers => OrderPending != null || PurchaseFailed != null || PurchaseDeferred != null || Disconnected != null;

		public UniTask<Result> ConnectAsync(CancellationToken cancellationToken)
		{
			ConnectCalls++;
			Calls.Add("connect");

			if (ConnectHandler != null)
			{
				return ConnectHandler();
			}

			IsConnected = true;
			return UniTask.FromResult(Result.Ok());
		}

		public UniTask<Result<IReadOnlyList<IapProduct>>> FetchProductsAsync(IReadOnlyList<IapProductDefinition> definitions,
			CancellationToken cancellationToken)
		{
			FetchProductsCalls++;
			Calls.Add("products");

			if (ProductsHandler != null)
			{
				return ProductsHandler();
			}

			var products = new List<IapProduct>();

			foreach (var definition in definitions)
			{
				if (!MissingProducts.Contains(definition.Id))
				{
					products.Add(Product(definition, !UnavailableProducts.Contains(definition.Id)));
				}
			}

			return UniTask.FromResult(Result<IReadOnlyList<IapProduct>>.Ok(products));
		}

		public UniTask<Result<IapStoreOrders>> FetchOrdersAsync(CancellationToken cancellationToken)
		{
			FetchOrdersCalls++;
			Calls.Add("orders");
			return OrdersHandler != null ? OrdersHandler() : UniTask.FromResult(Result<IapStoreOrders>.Ok(Snapshot()));
		}

		public void Purchase(IapProductDefinition product)
		{
			Purchases.Add(product);
			Calls.Add("purchase");
			OnPurchase?.Invoke(product);
		}

		public UniTask<IapConfirmResult> ConfirmAsync(IapStoreOrder order, CancellationToken cancellationToken)
		{
			ConfirmCalls.Add(order.TransactionId);
			ConfirmThreads.Add(Thread.CurrentThread.ManagedThreadId);
			ConfirmNatives.Add(order.Native);

			if (ConfirmHandler != null)
			{
				return ConfirmHandler(order);
			}

			Pending.RemoveAll(pending => pending.TransactionId == order.TransactionId);
			return UniTask.FromResult(IapConfirmResult.Confirmed());
		}

		public UniTask<Result> RestoreAsync(CancellationToken cancellationToken)
		{
			RestoreCalls++;
			return RestoreHandler != null ? RestoreHandler() : UniTask.FromResult(Result.Ok());
		}

		public UniTask<Result<IapEntitlementStatus>> CheckEntitlementAsync(IapProductDefinition product, CancellationToken cancellationToken)
		{
			return EntitlementHandler != null
				? EntitlementHandler(product)
				: UniTask.FromResult(Result<IapEntitlementStatus>.Ok(IapEntitlementStatus.NotEntitled));
		}

		public IapStoreOrders Snapshot()
		{
			return new IapStoreOrders(new List<IapStoreOrder>(Pending), new List<IapStoreOrder>(Owned), 0);
		}

		/// <summary>스토어에 미확정 주문을 만듭니다(이벤트는 올리지 않음).</summary>
		public IapStoreOrder AddPending(string productId, bool knownProduct = true, string transactionId = null)
		{
			var number = _nextTransaction++;
			var order = new IapStoreOrder(productId, transactionId ?? $"tx-{number}", $"receipt-{number}", knownProduct, $"native-{number}",
				$"jws-{number}");
			Pending.Add(order);
			return order;
		}

		/// <summary>결제를 끝내고 미확정 주문을 이벤트로 보냅니다.</summary>
		public IapStoreOrder CompletePurchase(string productId)
		{
			var order = AddPending(productId);
			RaiseOrderPending(order);
			return order;
		}

		public void RaiseOrderPending(IapStoreOrder order)
		{
			OrderPending?.Invoke(order);
		}

		public void RaisePurchaseFailed(string productId, IapStoreFailureReason reason, string message = "failed")
		{
			PurchaseFailed?.Invoke(new IapStorePurchaseFailure(productId, reason, message));
		}

		public void RaisePurchaseDeferred(string productId)
		{
			PurchaseDeferred?.Invoke(productId);
		}

		public void RaiseDisconnected(string message)
		{
			Disconnected?.Invoke(message);
		}

		public static IapProduct Product(IapProductDefinition definition, bool available = true)
		{
			return new IapProduct(definition, "₩1,100", 1100m, "KRW", $"{definition.Id} title", "description", available);
		}
	}

	/// <summary>게임 지급자 대신 쓰는 지급자입니다. 기본은 지급 성공이고, <see cref="Next"/> 에 넣은 결과를 순서대로 돌려줍니다.</summary>
	public sealed class ScriptedFulfiller : IPurchaseFulfiller
	{
		private readonly Queue<Func<IapOrder, CancellationToken, UniTask<FulfillmentResult>>> _next =
			new Queue<Func<IapOrder, CancellationToken, UniTask<FulfillmentResult>>>();

		public List<IapOrder> Calls { get; } = new List<IapOrder>();

		public List<CancellationToken> Tokens { get; } = new List<CancellationToken>();

		public Func<IapOrder, CancellationToken, UniTask<FulfillmentResult>> Default { get; set; } =
			(order, token) => UniTask.FromResult(FulfillmentResult.Granted("reward"));

		public UniTask<FulfillmentResult> FulfillAsync(IapOrder order, CancellationToken cancellationToken)
		{
			Calls.Add(order);
			Tokens.Add(cancellationToken);
			var handler = _next.Count > 0 ? _next.Dequeue() : Default;
			return handler(order, cancellationToken);
		}

		public void Next(FulfillmentResult result)
		{
			_next.Enqueue((order, token) => UniTask.FromResult(result));
		}

		public void Next(Func<IapOrder, CancellationToken, UniTask<FulfillmentResult>> handler)
		{
			_next.Enqueue(handler);
		}

		/// <summary>다음 호출을 테스트가 끝낼 때까지 붙잡습니다.</summary>
		public UniTaskCompletionSource<FulfillmentResult> Hold()
		{
			var source = new UniTaskCompletionSource<FulfillmentResult>();
			_next.Enqueue((order, token) => source.Task);
			return source;
		}
	}

	/// <summary>UniTask 의 결과를 붙잡아 둡니다(풀링된 UniTask 를 여러 번 읽지 않게).</summary>
	public sealed class Captured<T>
	{
		public bool Done { get; private set; }

		public T Value { get; private set; }

		public Exception Error { get; private set; }

		public static Captured<T> Of(UniTask<T> task)
		{
			var captured = new Captured<T>();
			captured.RunAsync(task).Forget();
			return captured;
		}

		private async UniTaskVoid RunAsync(UniTask<T> task)
		{
			try
			{
				Value = await task;
			}
			catch (Exception exception)
			{
				Error = exception;
			}

			Done = true;
		}
	}
}
