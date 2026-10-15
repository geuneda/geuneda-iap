using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	// 스토어에서 주문을 받아 큐에 넣는 쪽: 스토어 이벤트, 다시 조회, 복구
	public sealed partial class IapService
	{
		private readonly Dictionary<string, OrderEntry> _entries = new Dictionary<string, OrderEntry>();
		private readonly HashSet<string> _closed = new HashSet<string>();
		private readonly Dictionary<string, string> _unclaimed = new Dictionary<string, string>();
		private readonly List<OrderEntry> _pendingReports = new List<OrderEntry>();

		private FetchRequest _fetchCurrent;
		private FetchRequest _fetchNext;
		private int _fetchFailures;
		private CancellationTokenSource _recoveryTimer;

		private void OnStoreOrderPending(IapStoreOrder order)
		{
			if (!IsMainThread)
			{
				MainThreadDispatcher.Post(() => OnStoreOrderPending(order));
				return;
			}

			if (_disposed || order == null)
			{
				return;
			}

			var request = _purchase;
			var claims = request != null && request.AwaitingStore && Matches(order.ProductId, request.Definition);
			var entry = Accept(order, claims ? IapOrderOrigin.Purchase : IapOrderOrigin.Recovered, claims ? request.Payload : null, claims, out var ignored);

			if (ignored)
			{
				// 이번 실행에서 닫은 거래, 또는 이미 받은 거래가 종류를 모르거나 다른 상품으로 다시 왔습니다. 이 구매의 결과가 아닙니다
				claims = false;
			}

			// 구매 연결과 상태를 모두 바꾼 뒤에 바깥 코드(대기 결과, 이벤트)를 부릅니다. 처리기가 복구를 돌려도 이 주문은 이미 구매에 걸려 있습니다
			if (claims)
			{
				if (entry.State == OrderState.Unresolved)
				{
					EndStoreWait(request, null);
					// 결과를 받은 쪽이 곧바로 새 결제창을 열 수 있으니, 이 결제창이 닫혔다는 알림을 먼저 올립니다
					SetStoreUi(false);
					request.Waiter.Complete(Result<PurchaseSettlement>.Fail(IapResultCodes.UnknownProduct,
						$"The store does not know the product type of transaction {order.TransactionId}"));
				}
				else
				{
					ClaimForPurchase(entry, request);
					Bind(request, entry);
					EndStoreWait(request, entry);
					Enqueue(entry, true);
					SetStoreUi(false);
				}
			}

			RunLane();
			FlushReports();
		}

		private void OnStorePurchaseFailed(IapStorePurchaseFailure failure)
		{
			if (!IsMainThread)
			{
				MainThreadDispatcher.Post(() => OnStorePurchaseFailed(failure));
				return;
			}

			if (_disposed)
			{
				return;
			}

			var request = _purchase;
			var other = FindDefinition(failure.ProductId);

			if (request == null || !request.AwaitingStore || (other != null && other != request.Definition))
			{
				Logger.Warning($"Purchase failure with no waiting purchase: {failure.ProductId} {failure.Reason} {failure.Message}");
				return;
			}

			EndStoreWait(request, null);
			// 결과를 받은 쪽이 곧바로 새 결제창을 열 수 있으니, 이 결제창이 닫혔다는 알림을 결과보다 먼저 올립니다
			SetStoreUi(false);

			if (failure.Reason == IapStoreFailureReason.DuplicateTransaction)
			{
				Logger.Warning($"The store has an unfinished transaction of {request.Definition.Id}; fetching it to fulfill it first");
				ResolveDuplicateAsync(request).Forget();
			}
			else
			{
				var code = ToResultCode(failure.Reason);

				if (code == IapResultCodes.UserCancelled)
				{
					Logger.Info($"The user cancelled the purchase of {request.Definition.Id}");
				}
				else
				{
					Logger.Warning($"The store refused the purchase of {request.Definition.Id}: {failure.Reason} {failure.Message}");
				}

				request.Waiter.Complete(Result<PurchaseSettlement>.Fail(code, failure.Message ?? failure.Reason.ToString()));
			}

			RunLane();
		}

		private void OnStorePurchaseDeferred(string productId)
		{
			if (!IsMainThread)
			{
				MainThreadDispatcher.Post(() => OnStorePurchaseDeferred(productId));
				return;
			}

			if (_disposed)
			{
				return;
			}

			var request = _purchase;

			if (request == null || !request.AwaitingStore || !Matches(productId, request.Definition))
			{
				Logger.Info($"A payment of {productId} is waiting for approval");
				return;
			}

			EndStoreWait(request, null);
			_unclaimed[request.Definition.Id] = request.Payload;
			Logger.Info($"The payment of {request.Definition.Id} is waiting for approval; it will be fulfilled in the background once approved");
			SetStoreUi(false);
			request.Waiter.Complete(Result<PurchaseSettlement>.Fail(IapResultCodes.Deferred, "The payment is waiting for approval"));
			RunLane();
		}

		private void OnStoreDisconnected(string message)
		{
			if (!IsMainThread)
			{
				MainThreadDispatcher.Post(() => OnStoreDisconnected(message));
				return;
			}

			if (!_disposed)
			{
				Logger.Warning($"The store disconnected: {message}. The next operation reconnects");
			}
		}

		/// <summary>
		/// 결제창의 답을 더 기다리지 않습니다. 기다리는 동안 보류한 같은 상품의 새 주문 가운데 이 구매의 것이 아닌 것을 큐에 넣습니다. 이벤트는 올리지 않습니다.
		/// </summary>
		private void EndStoreWait(PurchaseRequest request, OrderEntry claimed)
		{
			request.AwaitingStore = false;
			StopStoreTimer(request);

			foreach (var held in request.Held)
			{
				if (held != claimed && held.State == OrderState.Held)
				{
					Enqueue(held, false);
				}
			}

			request.Held.Clear();
		}

		private async UniTaskVoid ResolveDuplicateAsync(PurchaseRequest request)
		{
			var found = await BindOpenOrderAsync(request);

			if (_disposed || request.Waiter.IsCompleted || found.Bound)
			{
				return;
			}

			request.Waiter.Complete(Result<PurchaseSettlement>.Fail(IapResultCodes.DuplicateTransaction, !found.Fetched.IsSuccess
				? $"Fetching the unfinished transaction failed: {found.Fetched}; the service retries the fetch in the background"
				: found.Closing
					? $"The earlier transaction of {request.Definition.Id} was fulfilled and is being closed; try again"
					: $"The store reported an unfinished transaction of {request.Definition.Id} but did not return it"));
		}

		/// <summary>
		/// 스토어를 다시 조회해 이 구매 상품의 지급 전 주문을 구매에 겁니다(큐가 돌기 전에 걸어야 바로 끝나는 지급의 결과도 받음).
		/// 지급은 끝났고 확정만 남은 주문은 큐 앞에 넣어 닫게 하고 걸지 않습니다(새 결제는 다시 사야 함).
		/// </summary>
		private async UniTask<(Result<IapStoreOrders> Fetched, bool Bound, bool Closing)> BindOpenOrderAsync(PurchaseRequest request)
		{
			var bound = false;
			var closing = false;
			var fetched = await FetchOrdersAsync(entries =>
			{
				if (request.Waiter.IsCompleted)
				{
					return;
				}

				foreach (var entry in entries)
				{
					if (entry.State == OrderState.Unresolved || entry.Order.Definition != request.Definition)
					{
						continue;
					}

					if (entry.Granted)
					{
						closing = true;
						Enqueue(entry, true);
						continue;
					}

					Bind(request, entry);
					Enqueue(entry, true);
					bound = true;
					return;
				}
			});

			if (!IsMainThread)
			{
				await UniTask.SwitchToMainThread();
			}

			return (fetched, bound, closing);
		}

		/// <summary>
		/// 스토어 주문을 거래 ID 로 하나의 항목에 모읍니다. 닫은 거래는 null 입니다. 새 항목은 큐에 넣고(결제창이 열린 상품의 조회 주문은 보류),
		/// 백오프 중이던 항목은 바로 다시 큐에 넣습니다. 바깥 코드를 부르지 않습니다(알림은 <see cref="FlushReports"/>).
		/// </summary>
		private OrderEntry Accept(IapStoreOrder storeOrder, IapOrderOrigin origin, string payload, bool front, out bool ignored)
		{
			ignored = false;

			if (_closed.Contains(storeOrder.TransactionId))
			{
				// 이번 실행에서 닫은 거래의 재전달입니다. 같은 상품의 새 구매가 결제창을 기다리고 있어도 그 결과가 아닙니다
				Logger.Debug($"Ignoring transaction {storeOrder.TransactionId}, which was closed in this session");
				ignored = true;
				return null;
			}

			var definition = FindDefinition(storeOrder.ProductId);

			if (_entries.TryGetValue(storeOrder.TransactionId, out var entry))
			{
				if (entry.State == OrderState.Unresolved)
				{
					entry.StoreOrder = storeOrder;

					if (TryResolve(entry))
					{
						// 새로 알게 된 주문도 새 주문과 같이 결제창이 열린 상품이면 보류합니다
						QueueOrHold(entry, front);
					}
					else
					{
						ReportUnresolved(entry);
					}

					return entry;
				}

				if (definition == null || definition != entry.Order.Definition || !storeOrder.IsKnownProduct)
				{
					// 종류를 모르거나 다른 상품으로 다시 온 주문으로 검증한 원본을 덮어쓰면, 확정 때 종류 모르는 원본을 넘기게 됩니다
					Logger.Warning($"Ignoring a redelivery of {entry.Order} as product '{storeOrder.ProductId}' " +
					               $"(known {storeOrder.IsKnownProduct}); keeping the validated order");
					ignored = true;
					return entry;
				}

				// 다음 지급 시도는 최신 영수증·서명으로 합니다(지급자에 넘기는 주문은 시도할 때 다시 만듦)
				entry.StoreOrder = storeOrder;

				if (entry.State == OrderState.Waiting || entry.State == OrderState.Queued)
				{
					Enqueue(entry, front);
				}

				return entry;
			}

			var request = _purchase;
			var awaitingThisProduct = request != null && request.AwaitingStore && definition != null && definition == request.Definition;

			if (origin == IapOrderOrigin.Recovered && !awaitingThisProduct && definition != null && _unclaimed.TryGetValue(definition.Id, out var latePayload))
			{
				// 기다리기를 그만둔(시간 초과·취소·승인 대기) 구매의 결제가 늦게 왔습니다
				_unclaimed.Remove(definition.Id);
				origin = IapOrderOrigin.Purchase;
				payload = latePayload;
			}

			entry = new OrderEntry(storeOrder, CreateOrder(definition, storeOrder, origin, payload))
			{
				Origin = origin,
				Payload = payload,
			};
			_entries.Add(storeOrder.TransactionId, entry);

			if (definition == null || !storeOrder.IsKnownProduct)
			{
				entry.State = OrderState.Unresolved;
				ReportUnresolved(entry);
				return entry;
			}

			Logger.Info($"Received {entry.Order}");
			QueueOrHold(entry, front);
			return entry;
		}

		/// <summary>
		/// 큐에 넣습니다. 다만 결제창이 열린 상품을 조회가 먼저 찾은 주문(구매 경로가 아닌 것)은 이 구매의 것일 수 있으니 결제창의 답이 올 때까지 보류합니다.
		/// 조회가 먼저 지급·확정하면 구매 이벤트가 닫힌 거래로 와서 구매 결과와 페이로드를 잃습니다.
		/// </summary>
		private void QueueOrHold(OrderEntry entry, bool front)
		{
			var request = _purchase;

			if (entry.Origin == IapOrderOrigin.Recovered && request != null && request.AwaitingStore && entry.Order.Definition == request.Definition)
			{
				entry.State = OrderState.Held;

				if (!request.Held.Contains(entry))
				{
					request.Held.Add(entry);
				}

				return;
			}

			Enqueue(entry, front);
		}

		/// <summary>이번 구매의 것으로 표시합니다(아직 지급자에 넘기기 전일 때만).</summary>
		private static void ClaimForPurchase(OrderEntry entry, PurchaseRequest request)
		{
			if (entry.Origin == IapOrderOrigin.Purchase || entry.Granted || entry.State == OrderState.Working)
			{
				return;
			}

			entry.Origin = IapOrderOrigin.Purchase;
			entry.Payload = request.Payload;
		}

		private bool TryResolve(OrderEntry entry)
		{
			var definition = FindDefinition(entry.StoreOrder.ProductId);

			if (definition == null || !entry.StoreOrder.IsKnownProduct || !_initialized)
			{
				return false;
			}

			entry.Order = CreateOrder(definition, entry.StoreOrder, entry.Origin, entry.Payload);
			return true;
		}

		private void ReportUnresolved(OrderEntry entry)
		{
			// 상품 조회 전에 온 주문은 스토어가 아직 종류를 모를 수 있어, 초기화 뒤 다시 조회해도 모를 때만 알립니다
			if (!_initialized || entry.UnresolvedReported)
			{
				return;
			}

			entry.UnresolvedReported = true;
			_pendingReports.Add(entry);
		}

		/// <summary>모아 둔 알림을 올립니다. 결과 자리를 모두 건 뒤에 불러, 처리기가 복구를 돌려도 결과를 놓치지 않게 합니다.</summary>
		private void FlushReports()
		{
			if (_pendingReports.Count == 0 || _disposed)
			{
				_pendingReports.Clear();
				return;
			}

			var reports = _pendingReports.ToArray();
			_pendingReports.Clear();

			foreach (var entry in reports)
			{
				var message = $"Transaction {entry.TransactionId} of product '{entry.StoreOrder.ProductId}' is unknown to the store or the definitions, " +
				              "so it is neither fulfilled nor confirmed. Keep retired products in IapServiceOptions.Products";
				Logger.Error(message);
				Raise(OrderFailed, new IapOrderFailure(entry.Order, IapResultCodes.UnknownProduct, message, 0, false), nameof(OrderFailed));
			}
		}

		private IapOrder CreateOrder(IapProductDefinition definition, IapStoreOrder storeOrder, IapOrderOrigin origin, string payload)
		{
			IapProduct product = null;

			if (definition != null)
			{
				_products.TryGetValue(definition.Id, out product);
			}

			return new IapOrder(definition, storeOrder.ProductId, product, storeOrder, _store.Name, origin, payload);
		}

		/// <summary>이 상품의 아직 닫지 못한 주문(종류를 모르는 주문 제외). 큐의 앞쪽 것을 고릅니다.</summary>
		private OrderEntry FindOpenEntry(string productId)
		{
			foreach (var entry in _queue)
			{
				if (entry.Order.Definition?.Id == productId)
				{
					return entry;
				}
			}

			foreach (var entry in _entries.Values)
			{
				if (entry.State != OrderState.Unresolved && entry.Order.Definition?.Id == productId)
				{
					return entry;
				}
			}

			return null;
		}

		private static void Bind(PurchaseRequest request, OrderEntry entry)
		{
			request.Entry = entry;
			entry.Purchase = request;

			if (!entry.Waiters.Contains(request.Waiter))
			{
				entry.Waiters.Add(request.Waiter);
			}
		}

		private static Waiter<Result<PurchaseSettlement>> AddWaiter(OrderEntry entry, CancellationToken cancellationToken)
		{
			var waiter = new Waiter<Result<PurchaseSettlement>>(cancellationToken,
				Result<PurchaseSettlement>.Fail(ResultCodes.Cancelled, "Wait cancelled"));
			entry.Waiters.Add(waiter);
			return waiter;
		}

		private static bool Matches(string productId, IapProductDefinition definition)
		{
			return productId != null && (productId == definition.Id || productId == definition.StoreSpecificId);
		}

		/// <summary>
		/// 스토어를 다시 조회합니다. 조회 중에 요청이 오면 지금 조회가 끝난 뒤 한 번 더 조회해 그 결과를 줍니다(요청 뒤에 시작한 조회만 믿음).
		/// 받은 미확정 주문은 모두 큐에 넣고, <paramref name="observer"/> 에 넘긴 뒤에 큐를 돌립니다. 결과가 필요한 쪽은 관찰자 안에서 결과 자리를 걸어야
		/// 바로 끝나는 지급의 결과도 받습니다(조회가 바로 끝나면 돌려받은 태스크를 기다리기 전에 큐가 돌 수 있음).
		/// </summary>
		private UniTask<Result<IapStoreOrders>> FetchOrdersAsync(Action<IReadOnlyList<OrderEntry>> observer = null)
		{
			if (_fetchCurrent == null)
			{
				// 조회가 바로 끝나면 RunFetchAsync 안에서 필드가 비워지므로 지역 변수로 돌려줍니다
				var request = new FetchRequest();
				request.Add(observer);
				_fetchCurrent = request;
				RunFetchAsync(request).Forget();
				return request.Source.Task;
			}

			_fetchNext ??= new FetchRequest();
			_fetchNext.Add(observer);
			return _fetchNext.Source.Task;
		}

		private static async UniTaskVoid ForwardAsync(FetchRequest from, FetchRequest to)
		{
			to.Source.TrySetResult(await from.Source.Task);
		}

		private async UniTaskVoid RunFetchAsync(FetchRequest request)
		{
			var source = request.Source;
			Result<IapStoreOrders> result;
			var connected = await EnsureConnectedAsync();

			if (!connected.IsSuccess)
			{
				result = Result<IapStoreOrders>.Fail(connected.Code, connected.Message);
			}
			else
			{
				var call = await CallAsync(token => _store.FetchOrdersAsync(token), _fetchTimeout, CancellationToken.None, "Fetching orders");
				result = ToResult(call);
			}

			if (_disposed)
			{
				source.TrySetResult(Result<IapStoreOrders>.Fail(ResultCodes.Cancelled, "IapService disposed"));
				return;
			}

			if (result.IsSuccess && result.Value == null)
			{
				result = Result<IapStoreOrders>.Fail(IapResultCodes.StoreError, "The store returned no order list");
			}

			var next = _fetchNext;
			_fetchNext = null;
			_fetchCurrent = null;

			if (result.IsSuccess)
			{
				_fetchFailures = 0;
				CancelRecoveryRetry();
				var orders = result.Value;
				var entries = new List<OrderEntry>(orders.Pending.Count);

				foreach (var order in orders.Pending)
				{
					var entry = order == null ? null : Accept(order, IapOrderOrigin.Recovered, null, false, out _);

					if (entry != null)
					{
						entries.Add(entry);
					}
				}

				if (orders.Pending.Count > 0 || orders.DeferredCount > 0)
				{
					Logger.Info($"Fetched {orders.Pending.Count} unfinished and {orders.DeferredCount} deferred order(s)");
				}

				// 큐를 돌리기 전에 기다리는 쪽(복구, 중복 거래)이 결과 자리를 겁니다
				foreach (var observer in request.Observers)
				{
					observer(entries);
				}
			}
			else
			{
				Logger.Warning($"Fetching orders failed: {result}");

				if (result.Code != ResultCodes.Cancelled && _initialized)
				{
					// 조회를 못 하면 남은 결제를 찾을 수 없으므로, 앱이 켜져 있는 동안 백오프로 다시 조회합니다
					ScheduleRecoveryRetry();
				}
			}

			source.TrySetResult(result);
			RunLane();
			FlushReports();

			if (next == null || _disposed)
			{
				next?.Source.TrySetResult(Result<IapStoreOrders>.Fail(ResultCodes.Cancelled, "IapService disposed"));
				return;
			}

			if (_fetchCurrent == null)
			{
				_fetchCurrent = next;
				RunFetchAsync(next).Forget();
			}
			else
			{
				// 결과를 받은 쪽이 이미 새 조회를 시작했습니다. 그 조회도 요청 뒤에 시작했으므로 관찰자를 옮기고 같은 결과를 받습니다
				_fetchCurrent.Observers.AddRange(next.Observers);
				ForwardAsync(_fetchCurrent, next).Forget();
			}
		}

		/// <summary>조회 한 번과 그 결과를 기다리는 관찰자입니다.</summary>
		private sealed class FetchRequest
		{
			public UniTaskCompletionSource<Result<IapStoreOrders>> Source { get; } = new UniTaskCompletionSource<Result<IapStoreOrders>>();

			public List<Action<IReadOnlyList<OrderEntry>>> Observers { get; } = new List<Action<IReadOnlyList<OrderEntry>>>();

			public void Add(Action<IReadOnlyList<OrderEntry>> observer)
			{
				if (observer != null)
				{
					Observers.Add(observer);
				}
			}
		}

		private void StartRecovery(string reason)
		{
			RecoverInBackgroundAsync(reason).Forget();
		}

		private async UniTaskVoid RecoverInBackgroundAsync(string reason)
		{
			var recovery = await RecoverCoreAsync(false, CancellationToken.None);

			if (_disposed)
			{
				return;
			}

			if (!recovery.Summary.IsSuccess)
			{
				Logger.Warning($"Recovery after {reason} failed: {recovery.Summary}");
			}
			else if (recovery.Summary.Value.Total > 0)
			{
				Logger.Info($"Recovery after {reason}: {recovery.Summary.Value}");
			}
		}

		/// <summary>스토어를 다시 조회하고, 받은 미확정 주문의 이번 처리 결과를 모읍니다.</summary>
		/// <param name="includeRejected">거절했던 주문도 다시 지급자에 넘길지(사용자나 게임이 명시적으로 복구할 때)</param>
		private async UniTask<(Result<RecoverySummary> Summary, IapStoreOrders Orders)> RecoverCoreAsync(bool includeRejected,
			CancellationToken cancellationToken)
		{
			var waits = new List<(OrderEntry Entry, Waiter<Result<PurchaseSettlement>> Waiter)>();
			var rejected = 0;
			var unresolved = 0;

			void Observe(IReadOnlyList<OrderEntry> entries)
			{
				foreach (var entry in entries)
				{
					if (entry.State == OrderState.Rejected && includeRejected)
					{
						Enqueue(entry, false);
					}

					switch (entry.State)
					{
						case OrderState.Queued:
						case OrderState.Working:
						case OrderState.Held:
							waits.Add((entry, AddWaiter(entry, cancellationToken)));
							break;
						case OrderState.Rejected:
							rejected++;
							break;
						case OrderState.Unresolved:
							unresolved++;
							break;
					}
				}
			}

			var cancel = new Waiter<Result<IapStoreOrders>>(cancellationToken, Result<IapStoreOrders>.Fail(ResultCodes.Cancelled, "Recovery cancelled"));
			var (winner, fetchedOrders, cancelled) = await UniTask.WhenAny(FetchOrdersAsync(Observe), cancel.Task);
			var fetched = winner == 0 ? fetchedOrders : cancelled;

			if (!IsMainThread)
			{
				await UniTask.SwitchToMainThread();
			}

			cancel.Release();

			if (_disposed)
			{
				return (Result<RecoverySummary>.Fail(ResultCodes.Cancelled, "IapService disposed"), null);
			}

			if (!fetched.IsSuccess)
			{
				return (Result<RecoverySummary>.Fail(fetched.Code, fetched.Message), null);
			}

			var results = new Result<PurchaseSettlement>[waits.Count];

			for (var i = 0; i < waits.Count; i++)
			{
				results[i] = await waits[i].Waiter.Task;
			}

			if (!IsMainThread)
			{
				await UniTask.SwitchToMainThread();
			}

			foreach (var (entry, waiter) in waits)
			{
				entry.Waiters.Remove(waiter);
				waiter.Release();
			}

			if (_disposed || cancellationToken.IsCancellationRequested)
			{
				return (Result<RecoverySummary>.Fail(ResultCodes.Cancelled, "Recovery cancelled"), null);
			}

			var granted = 0;
			var retrying = 0;

			foreach (var result in results)
			{
				if (result.IsSuccess)
				{
					granted++;
				}
				else if (result.Code == IapResultCodes.FulfillmentRejected)
				{
					rejected++;
				}
				else if (result.Code == IapResultCodes.UnknownProduct)
				{
					unresolved++;
				}
				else
				{
					retrying++;
				}
			}

			return (Result<RecoverySummary>.Ok(new RecoverySummary(granted, retrying, rejected, unresolved)), fetched.Value);
		}

		/// <summary>실패한 조회를 백오프(지급 재시도와 같은 간격) 뒤 다시 합니다. 이미 예약돼 있으면 그대로 둡니다.</summary>
		private void ScheduleRecoveryRetry()
		{
			_fetchFailures++;

			if (_recoveryTimer != null || _disposed)
			{
				return;
			}

			var timer = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
			_recoveryTimer = timer;
			RecoveryRetryAsync(timer, RetryDelayFor(_fetchFailures)).Forget();
		}

		private async UniTaskVoid RecoveryRetryAsync(CancellationTokenSource timer, TimeSpan delay)
		{
			try
			{
				await _delay(delay, timer.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			if (_disposed || _recoveryTimer != timer)
			{
				return;
			}

			_recoveryTimer = null;
			timer.Dispose();
			StartRecovery("a failed fetch");
		}

		private void CancelRecoveryRetry()
		{
			var timer = _recoveryTimer;
			_recoveryTimer = null;

			if (timer != null)
			{
				timer.Cancel();
				timer.Dispose();
			}
		}

		private static int ToResultCode(IapStoreFailureReason reason)
		{
			switch (reason)
			{
				case IapStoreFailureReason.StoreUnavailable:
					return IapResultCodes.StoreUnavailable;
				case IapStoreFailureReason.ExistingPurchasePending:
					return IapResultCodes.ExistingPurchasePending;
				case IapStoreFailureReason.ProductUnavailable:
					return IapResultCodes.ProductUnavailable;
				case IapStoreFailureReason.ValidationFailed:
					return IapResultCodes.StoreValidationFailed;
				case IapStoreFailureReason.UserCancelled:
					return IapResultCodes.UserCancelled;
				case IapStoreFailureReason.PaymentDeclined:
					return IapResultCodes.PaymentDeclined;
				case IapStoreFailureReason.DuplicateTransaction:
					return IapResultCodes.DuplicateTransaction;
				default:
					return IapResultCodes.StoreError;
			}
		}
	}
}
