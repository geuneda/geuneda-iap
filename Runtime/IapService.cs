using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using UnityEngine;
using Object = UnityEngine.Object;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// <see cref="IIapService"/> 의 기본 구현입니다. 스토어 어댑터(<see cref="IIapStore"/>)와 게임의 지급자(<see cref="IPurchaseFulfiller"/>)를 받아,
	/// 결제한 주문을 하나씩 지급하고 지급이 확인된 주문만 스토어 거래를 닫습니다.
	/// </summary>
	/// <remarks>
	/// <para>
	/// 지급 누락을 막는 규칙: 지급 확인 전에는 확정하지 않습니다. 지급자가 나중에를 돌려주거나 시간 초과·예외가 나면 거래를 열어 두고 백오프(5초부터 두 배씩 5분까지)로
	/// 다시 지급합니다. 초기화, 포그라운드 복귀, <see cref="RecoverAsync"/> 때마다 스토어를 다시 조회해 남은 거래를 처리합니다(캐시만 보지 않음).
	/// 결제창이 제한 시간 안에 답하지 않아도 늦게 온 결제는 뒤에서 지급합니다. 지급 뒤 확정만 실패하면 지급자를 다시 부르지 않고 확정만 다시 시도합니다.
	/// </para>
	/// <para>
	/// 구매·복원은 한 번에 하나이고, 지급·확정은 한 줄 큐에서 주문 하나씩 처리합니다(같은 거래가 동시에 지급되지 않음). 거래 ID 로 중복을 거릅니다.
	/// </para>
	/// </remarks>
	public sealed partial class IapService : IIapService, IDisposable
	{
		private static readonly ModuleLogger Logger = new ModuleLogger("Geuneda.Iap");

		private readonly IIapStore _store;
		private readonly IPurchaseFulfiller _fulfiller;
		private readonly IMonotonicClock _clock;
		private readonly Func<TimeSpan, CancellationToken, UniTask> _delay;
		private readonly Func<double> _random;
		private readonly int _threadId;
		private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
		private readonly IReadOnlyList<IapProductDefinition> _definitionList;
		private readonly Dictionary<string, IapProductDefinition> _definitions = new Dictionary<string, IapProductDefinition>();
		private readonly Dictionary<string, IapProductDefinition> _definitionsByStoreId = new Dictionary<string, IapProductDefinition>();
		private readonly Dictionary<string, IapProduct> _products = new Dictionary<string, IapProduct>();
		private readonly List<IapProduct> _productList = new List<IapProduct>();
		private readonly List<Waiter<Result>> _initWaiters = new List<Waiter<Result>>();
		private readonly TimeSpan _connectTimeout;
		private readonly TimeSpan _fetchTimeout;
		private readonly TimeSpan _storeResponseTimeout;
		private readonly TimeSpan _fulfillTimeout;
		private readonly TimeSpan _confirmTimeout;
		private readonly TimeSpan _retryDelay;
		private readonly TimeSpan _maxRetryDelay;
		private readonly bool _recoverOnResume;

		private UniTaskCompletionSource<Result> _connecting;
		private PurchaseRequest _purchase;
		private GameObject _lifecycleObject;
		private bool _initializing;
		private bool _initializeAttempted;
		private bool _initialized;
		private bool _restoring;
		private bool _storeUiOpen;
		private bool _paused;
		private bool _disposed;

		/// <summary>서비스를 만듭니다. 메인 스레드에서 부르고, 끝나면 <see cref="Dispose"/> 합니다(스토어 어댑터는 직접 정리).</summary>
		/// <exception cref="ArgumentNullException">인자가 null</exception>
		/// <exception cref="ArgumentException">상품 목록이 비었거나 ID 가 겹침</exception>
		/// <exception cref="ArgumentOutOfRangeException">시간 설정이 범위를 벗어남</exception>
		public IapService(IIapStore store, IPurchaseFulfiller fulfiller, IapServiceOptions options)
			: this(store, fulfiller, options, new MonotonicClock(), DefaultDelay, CreateRandom(), Application.isPlaying)
		{
		}

		internal IapService(IIapStore store, IPurchaseFulfiller fulfiller, IapServiceOptions options, IMonotonicClock clock,
			Func<TimeSpan, CancellationToken, UniTask> delay, Func<double> random, bool bindLifecycle)
		{
			_store = store ?? throw new ArgumentNullException(nameof(store));
			_fulfiller = fulfiller ?? throw new ArgumentNullException(nameof(fulfiller));

			if (options == null)
			{
				throw new ArgumentNullException(nameof(options));
			}

			Validate(options);

			foreach (var definition in options.Products)
			{
				_definitions.Add(definition.Id, definition);
				_definitionsByStoreId.Add(definition.StoreSpecificId, definition);
			}

			_definitionList = new List<IapProductDefinition>(options.Products);
			_clock = clock;
			_delay = delay;
			_random = random;
			_threadId = Thread.CurrentThread.ManagedThreadId;
			_connectTimeout = options.ConnectTimeout;
			_fetchTimeout = options.FetchTimeout;
			_storeResponseTimeout = options.StoreResponseTimeout;
			_fulfillTimeout = options.FulfillTimeout;
			_confirmTimeout = options.ConfirmTimeout;
			_retryDelay = options.RetryDelay;
			_maxRetryDelay = options.MaxRetryDelay;
			_recoverOnResume = options.RecoverOnResume;

			_store.OrderPending += OnStoreOrderPending;
			_store.PurchaseFailed += OnStorePurchaseFailed;
			_store.PurchaseDeferred += OnStorePurchaseDeferred;
			_store.Disconnected += OnStoreDisconnected;

			if (bindLifecycle)
			{
				_lifecycleObject = new GameObject(nameof(IapLifecycle)) { hideFlags = HideFlags.HideInHierarchy };
				Object.DontDestroyOnLoad(_lifecycleObject);
				_lifecycleObject.AddComponent<IapLifecycle>().Bind(this);
			}
		}

		public bool IsInitialized => _initialized;

		public IReadOnlyList<IapProduct> Products => _productList;

		public bool IsStoreUiOpen => _storeUiOpen;

		public event Action ProductsUpdated;

		public event Action<bool> StoreUiChanged;

		public event Action<PurchaseSettlement> OrderSettled;

		public event Action<IapOrderFailure> OrderFailed;

		public event Action<PurchaseSettlement> PurchaseConfirmed;

		internal GameObject LifecycleObject => _lifecycleObject;

		private bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _threadId;

		public bool TryGetProduct(string productId, out IapProduct product)
		{
			ThrowIfUnusable();

			if (productId == null)
			{
				product = null;
				return false;
			}

			return _products.TryGetValue(productId, out product);
		}

		public async UniTask<Result> InitializeAsync(CancellationToken cancellationToken = default)
		{
			ThrowIfUnusable();

			if (_initialized)
			{
				return Result.Ok();
			}

			var waiter = new Waiter<Result>(cancellationToken, Result.Fail(ResultCodes.Cancelled, "Initialization wait cancelled"));
			_initWaiters.Add(waiter);

			if (!_initializing)
			{
				RunInitializeAsync().Forget();
			}

			var result = await waiter.Task;

			if (!IsMainThread)
			{
				await UniTask.SwitchToMainThread();
			}

			_initWaiters.Remove(waiter);
			waiter.Release();
			return result;
		}

		public async UniTask<Result<PurchaseSettlement>> PurchaseAsync(string productId, string payload = null, CancellationToken cancellationToken = default)
		{
			ThrowIfUnusable();
			var definition = GetDefinition(productId);

			if (cancellationToken.IsCancellationRequested)
			{
				return Result<PurchaseSettlement>.Fail(ResultCodes.Cancelled, "Purchase cancelled before it started");
			}

			if (!_initialized)
			{
				return Result<PurchaseSettlement>.Fail(IapResultCodes.NotInitialized, "IapService is not initialized");
			}

			if (_purchase != null || _restoring)
			{
				return Result<PurchaseSettlement>.Fail(ResultCodes.InProgress, "Another purchase or restore is in progress");
			}

			var request = new PurchaseRequest(definition, payload, cancellationToken);
			_purchase = request;

			try
			{
				var existing = FindOpenEntry(definition.Id);

				if (existing != null && existing.Granted)
				{
					// 지급은 끝났고 확정만 남은 이전 결제입니다. 확정을 먼저 해야 같은 상품을 다시 살 수 있습니다(Google)
					var closed = await CloseGrantedEntryAsync(existing, request);

					if (!closed.IsSuccess)
					{
						return closed;
					}

					existing = null;
				}

				var bound = false;

				if (existing != null)
				{
					Logger.Info($"Fulfilling the unfinished order {existing.Order} instead of opening the store");
					Bind(request, existing);
					Enqueue(existing, true);
					RunLane();
					bound = true;
				}
				else if (_unclaimed.ContainsKey(definition.Id))
				{
					// 같은 상품의 이전 구매가 스토어의 답 없이 끝났습니다. 새 결제를 열면 늦게 온 결제가 어느 구매의 것인지 가를 수 없으므로,
					// 다시 조회해 그 결제가 와 있으면 먼저 지급하고(이전 구매의 페이로드로), 없을 때만 이전 페이로드를 버리고 결제창을 엽니다
					var found = await BindOpenOrderAsync(request);

					if (found.Bound)
					{
						RunLane();
						bound = true;
					}
					else if (request.Waiter.IsCompleted)
					{
						return await request.Waiter.Task;
					}
					else if (!found.Fetched.IsSuccess)
					{
						return Result<PurchaseSettlement>.Fail(found.Fetched.Code,
							$"Could not check the earlier unanswered purchase of {definition.Id}: {found.Fetched}");
					}
					else
					{
						_unclaimed.Remove(definition.Id);
						Logger.Warning($"The earlier unanswered purchase of {definition.Id} has no payment in the store; dropping its payload");
					}
				}

				if (!bound)
				{
					var opened = await OpenStoreAsync(request);

					if (!opened.IsSuccess)
					{
						return opened;
					}
				}

				var result = await request.Waiter.Task;

				if (!IsMainThread)
				{
					await UniTask.SwitchToMainThread();
				}

				return result;
			}
			finally
			{
				ReleasePurchase(request);
			}
		}

		public async UniTask<Result<RecoverySummary>> RecoverAsync(CancellationToken cancellationToken = default)
		{
			ThrowIfUnusable();

			if (cancellationToken.IsCancellationRequested)
			{
				return Result<RecoverySummary>.Fail(ResultCodes.Cancelled, "Recovery cancelled before it started");
			}

			if (!_initialized)
			{
				return Result<RecoverySummary>.Fail(IapResultCodes.NotInitialized, "IapService is not initialized");
			}

			var recovery = await RecoverCoreAsync(true, cancellationToken);
			return recovery.Summary;
		}

		public async UniTask<Result<IReadOnlyList<IapOrder>>> RestoreAsync(CancellationToken cancellationToken = default)
		{
			ThrowIfUnusable();

			if (cancellationToken.IsCancellationRequested)
			{
				return Result<IReadOnlyList<IapOrder>>.Fail(ResultCodes.Cancelled, "Restore cancelled before it started");
			}

			if (!_initialized)
			{
				return Result<IReadOnlyList<IapOrder>>.Fail(IapResultCodes.NotInitialized, "IapService is not initialized");
			}

			if (_purchase != null || _restoring)
			{
				return Result<IReadOnlyList<IapOrder>>.Fail(ResultCodes.InProgress, "Another purchase or restore is in progress");
			}

			_restoring = true;

			try
			{
				var connected = await EnsureConnectedAsync();

				if (!connected.IsSuccess)
				{
					return Result<IReadOnlyList<IapOrder>>.Fail(connected.Code, connected.Message);
				}

				CallResult<Result> call;
				SetStoreUi(true);

				try
				{
					// 처리기가 서비스를 닫았거나 호출자가 취소했으면 복원 요청을 보내지 않습니다
					if (_disposed || cancellationToken.IsCancellationRequested)
					{
						return Result<IReadOnlyList<IapOrder>>.Fail(ResultCodes.Cancelled, "Restore cancelled");
					}

					call = await CallAsync(token => _store.RestoreAsync(token), _storeResponseTimeout, cancellationToken, "Restoring purchases");
				}
				finally
				{
					SetStoreUi(false);
				}

				var restored = ToResult(call);

				if (!restored.IsSuccess)
				{
					return Result<IReadOnlyList<IapOrder>>.Fail(restored.Code, restored.Message);
				}

				var recovery = await RecoverCoreAsync(true, cancellationToken);

				if (!recovery.Summary.IsSuccess)
				{
					return Result<IReadOnlyList<IapOrder>>.Fail(recovery.Summary.Code, recovery.Summary.Message);
				}

				var owned = new List<IapOrder>();

				foreach (var storeOrder in recovery.Orders.Owned)
				{
					var definition = FindDefinition(storeOrder?.ProductId);

					if (definition != null && definition.Type != IapProductType.Consumable)
					{
						owned.Add(CreateOrder(definition, storeOrder, IapOrderOrigin.Restored, null));
					}
				}

				Logger.Info($"Restore finished: {recovery.Summary.Value}, owned {owned.Count}");
				return Result<IReadOnlyList<IapOrder>>.Ok(owned);
			}
			finally
			{
				_restoring = false;
			}
		}

		public async UniTask<Result<IapEntitlementStatus>> CheckEntitlementAsync(string productId, CancellationToken cancellationToken = default)
		{
			ThrowIfUnusable();
			var definition = GetDefinition(productId);

			if (cancellationToken.IsCancellationRequested)
			{
				return Result<IapEntitlementStatus>.Fail(ResultCodes.Cancelled, "Entitlement check cancelled before it started");
			}

			if (!_initialized)
			{
				return Result<IapEntitlementStatus>.Fail(IapResultCodes.NotInitialized, "IapService is not initialized");
			}

			var connected = await EnsureConnectedAsync();

			if (!connected.IsSuccess)
			{
				return Result<IapEntitlementStatus>.Fail(connected.Code, connected.Message);
			}

			var call = await CallAsync(token => _store.CheckEntitlementAsync(definition, token), _fetchTimeout, cancellationToken,
				$"Checking the entitlement of {definition.Id}");
			return ToResult(call);
		}

		/// <summary>
		/// 서비스를 끝냅니다. 기다리는 호출은 <see cref="ResultCodes.Cancelled"/> 로 끝나고, 열린 거래는 스토어에 남아 다음 실행에서 처리됩니다.
		/// </summary>
		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			ThrowIfWrongThread();
			_disposed = true;

			_store.OrderPending -= OnStoreOrderPending;
			_store.PurchaseFailed -= OnStorePurchaseFailed;
			_store.PurchaseDeferred -= OnStorePurchaseDeferred;
			_store.Disconnected -= OnStoreDisconnected;

			CancelQuietly(_lifetime, "IapService lifetime");
			CancelWake();
			CancelRecoveryRetry();
			DestroyLifecycle();
			_pendingReports.Clear();

			var cancelled = Result<PurchaseSettlement>.Fail(ResultCodes.Cancelled, "IapService disposed");
			var orderWaiters = new List<Waiter<Result<PurchaseSettlement>>>();

			foreach (var entry in _entries.Values)
			{
				orderWaiters.AddRange(entry.Waiters);
				entry.Waiters.Clear();
			}

			if (_purchase != null)
			{
				orderWaiters.Add(_purchase.Waiter);
			}

			var initWaiters = _initWaiters.ToArray();
			_initWaiters.Clear();
			_queue.Clear();
			var connecting = _connecting;
			_connecting = null;
			var fetches = new[] { _fetchCurrent, _fetchNext };
			_fetchCurrent = null;
			_fetchNext = null;

			if (_storeUiOpen)
			{
				_storeUiOpen = false;
				Raise(StoreUiChanged, false, nameof(StoreUiChanged));
			}

			// 상태를 다 바꾼 뒤 끝내야, 결과를 받은 호출자가 이어서 부르는 코드가 닫힌 서비스를 봅니다
			foreach (var waiter in orderWaiters)
			{
				waiter.Complete(cancelled);
			}

			foreach (var waiter in initWaiters)
			{
				waiter.Complete(Result.Fail(ResultCodes.Cancelled, "IapService disposed"));
			}

			connecting?.TrySetResult(Result.Fail(ResultCodes.Cancelled, "IapService disposed"));

			foreach (var fetch in fetches)
			{
				fetch?.Source.TrySetResult(Result<IapStoreOrders>.Fail(ResultCodes.Cancelled, "IapService disposed"));
			}
		}

		internal void HandlePause(bool paused)
		{
			if (_disposed)
			{
				return;
			}

			if (paused)
			{
				_paused = true;
				return;
			}

			if (!_paused)
			{
				return;
			}

			_paused = false;

			if (!_initialized)
			{
				// 시작 때 네트워크가 없어 초기화가 실패했으면 복귀할 때 다시 합니다(그래야 남은 결제가 복구됨)
				if (_initializeAttempted && !_initializing)
				{
					Logger.Info("Retrying initialization after resume");
					RunInitializeAsync().Forget();
				}

				return;
			}

			// 결제창·복원에서 돌아온 복귀는 그 흐름이 결과를 처리하므로 다시 조회하지 않습니다
			if (_recoverOnResume && _purchase == null && !_restoring)
			{
				StartRecovery("resume");
			}
		}

		private async UniTaskVoid RunInitializeAsync()
		{
			_initializing = true;
			_initializeAttempted = true;
			Result result;

			try
			{
				result = await InitializeCoreAsync();
			}
			catch (Exception exception)
			{
				// 서비스 버그입니다. 기다리는 호출자에게 예외로 넘겨 숨기지 않습니다
				_initializing = false;
				var failed = _initWaiters.ToArray();
				_initWaiters.Clear();

				foreach (var waiter in failed)
				{
					waiter.Fail(exception);
				}

				throw;
			}

			_initializing = false;

			if (_disposed)
			{
				return;
			}

			// 기다리는 호출자는 바깥 코드(이벤트, 복구)를 부른 뒤에 끝냅니다. 그 사이 처리기가 서비스를 닫으면 Dispose 가 목록에 남은 호출자를 취소로 끝냅니다
			if (result.IsSuccess)
			{
				Logger.Info($"Initialized with {_productList.Count} product(s) from {_store.Name}");
				Raise(ProductsUpdated, nameof(ProductsUpdated));

				if (!_disposed)
				{
					StartRecovery("initialization");
				}
			}
			else
			{
				Logger.Warning($"Initialization failed: {result}");
			}

			var waiters = _initWaiters.ToArray();
			_initWaiters.Clear();

			foreach (var waiter in waiters)
			{
				waiter.Complete(result);
			}
		}

		private async UniTask<Result> InitializeCoreAsync()
		{
			var connected = await EnsureConnectedAsync();

			if (!connected.IsSuccess)
			{
				return connected;
			}

			var call = await CallAsync(token => _store.FetchProductsAsync(_definitionList, token), _fetchTimeout, CancellationToken.None,
				"Fetching products");
			var fetched = ToResult(call);

			if (_disposed)
			{
				return Result.Fail(ResultCodes.Cancelled, "IapService disposed");
			}

			if (!fetched.IsSuccess)
			{
				return Result.Fail(fetched.Code, fetched.Message);
			}

			_products.Clear();
			_productList.Clear();

			foreach (var product in fetched.Value ?? Array.Empty<IapProduct>())
			{
				if (product != null && _definitions.ContainsKey(product.Id) && !_products.ContainsKey(product.Id))
				{
					_products.Add(product.Id, product);
					_productList.Add(product);
				}
			}

			if (_productList.Count == 0)
			{
				return Result.Fail(IapResultCodes.NoProducts, "The store returned none of the defined products");
			}

			if (_productList.Count < _definitionList.Count)
			{
				var missing = new List<string>();

				foreach (var definition in _definitionList)
				{
					if (!_products.ContainsKey(definition.Id))
					{
						missing.Add(definition.Id);
					}
				}

				Logger.Warning($"The store did not return {missing.Count} product(s): {string.Join(", ", missing)}");
			}

			_initialized = true;
			return Result.Ok();
		}

		private async UniTask<Result> EnsureConnectedAsync()
		{
			if (_store.IsConnected)
			{
				return Result.Ok();
			}

			if (_connecting != null)
			{
				return await _connecting.Task;
			}

			var source = _connecting = new UniTaskCompletionSource<Result>();
			var call = await CallAsync(token => _store.ConnectAsync(token), _connectTimeout, CancellationToken.None, "Connecting to the store");
			var result = ToResult(call);

			if (result.IsSuccess && !_store.IsConnected)
			{
				result = Result.Fail(IapResultCodes.StoreUnavailable, "The store reported a connection but is not connected");
			}
			else if (!result.IsSuccess && result.Code != ResultCodes.Cancelled && result.Code != IapResultCodes.StoreUnavailable)
			{
				result = Result.Fail(IapResultCodes.StoreUnavailable, $"Connecting to the store failed: {result}");
			}

			if (_connecting == source)
			{
				_connecting = null;
			}

			source.TrySetResult(result);
			return result;
		}

		private async UniTask<Result<PurchaseSettlement>> OpenStoreAsync(PurchaseRequest request)
		{
			var definition = request.Definition;

			if (!_products.TryGetValue(definition.Id, out var product))
			{
				return Result<PurchaseSettlement>.Fail(IapResultCodes.ProductNotFound, $"The store did not return {definition.Id}");
			}

			if (!product.IsAvailable)
			{
				return Result<PurchaseSettlement>.Fail(IapResultCodes.ProductUnavailable, $"{definition.Id} is not available");
			}

			var (winner, connected, cancelled) = await UniTask.WhenAny(EnsureConnectedAsync(), request.Waiter.Task);

			if (!IsMainThread)
			{
				await UniTask.SwitchToMainThread();
			}

			if (winner != 0)
			{
				return cancelled;
			}

			if (_disposed)
			{
				return Result<PurchaseSettlement>.Fail(ResultCodes.Cancelled, "IapService disposed");
			}

			if (!connected.IsSuccess)
			{
				return Result<PurchaseSettlement>.Fail(connected.Code, connected.Message);
			}

			request.AwaitingStore = true;
			StartStoreTimer(request);
			SetStoreUi(true);

			// 처리기가 서비스를 닫았거나 호출자가 취소했으면 결제를 시작하지 않습니다
			if (_disposed || request.Waiter.IsCompleted)
			{
				EndStoreWait(request, null);
				SetStoreUi(false);
				return Result<PurchaseSettlement>.Ok(null);
			}

			Logger.Info($"Opening the store for {definition.Id}");
			_store.Purchase(definition);
			return Result<PurchaseSettlement>.Ok(null);
		}

		private async UniTask<Result<PurchaseSettlement>> CloseGrantedEntryAsync(OrderEntry entry, PurchaseRequest request)
		{
			Logger.Info($"Closing the fulfilled order {entry.Order} before buying {request.Definition.Id} again");
			var waiter = AddWaiter(entry, CancellationToken.None);
			Enqueue(entry, true);
			RunLane();
			var (winner, result, cancelled) = await UniTask.WhenAny(waiter.Task, request.Waiter.Task);

			if (!IsMainThread)
			{
				await UniTask.SwitchToMainThread();
			}

			entry.Waiters.Remove(waiter);

			if (winner != 0)
			{
				return cancelled;
			}

			if (!result.IsSuccess || !result.Value.IsConfirmed)
			{
				return Result<PurchaseSettlement>.Fail(IapResultCodes.ExistingPurchasePending,
					$"The previous purchase of {request.Definition.Id} is fulfilled but could not be closed yet: {result}");
			}

			return Result<PurchaseSettlement>.Ok(null);
		}

		private void ReleasePurchase(PurchaseRequest request)
		{
			if (_purchase == request)
			{
				_purchase = null;
			}

			if (request.AwaitingStore)
			{
				// 결제창이 열린 동안 조회가 찾은 이 상품의 새 주문이 하나면 이 구매의 결제입니다(스토어가 이벤트로 답하지 않았을 뿐)
				var claimed = !_disposed && request.Held.Count == 1 ? request.Held[0] : null;

				if (claimed != null)
				{
					ClaimForPurchase(claimed, request);
				}

				EndStoreWait(request, claimed);

				if (claimed != null)
				{
					Enqueue(claimed, true);
				}
				else if (!_disposed)
				{
					// 기다리기를 그만둔 뒤 오는 결제도 이 구매의 것으로 지급합니다
					_unclaimed[request.Definition.Id] = request.Payload;
					Logger.Warning($"Stopped waiting for the store's answer about {request.Definition.Id}; a late payment will be fulfilled in the background");
				}

				SetStoreUi(false);
				RunLane();

				if (claimed == null && !_disposed)
				{
					// 스토어에는 결제가 있는데 이벤트만 놓친 경우를 찾습니다(결제창에서 돌아온 복귀는 재조회를 건너뛰었음). 조회 실패는 백오프로 다시 합니다
					StartRecovery("an unanswered purchase");
				}
			}
			else
			{
				StopStoreTimer(request);
			}

			var entry = request.Entry;

			if (entry != null)
			{
				entry.Waiters.Remove(request.Waiter);

				if (entry.Purchase == request)
				{
					entry.Purchase = null;
				}
			}

			request.Waiter.Release();
		}

		private void StartStoreTimer(PurchaseRequest request)
		{
			var timer = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
			request.StoreTimer = timer;
			StoreTimeoutAsync(request, timer.Token).Forget();
		}

		private async UniTaskVoid StoreTimeoutAsync(PurchaseRequest request, CancellationToken token)
		{
			try
			{
				await _delay(_storeResponseTimeout, token);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			if (_disposed || !request.AwaitingStore)
			{
				return;
			}

			request.Waiter.Complete(Result<PurchaseSettlement>.Fail(ResultCodes.Timeout,
				$"The store did not answer within {_storeResponseTimeout}; a late payment will be fulfilled in the background"));
		}

		private static void StopStoreTimer(PurchaseRequest request)
		{
			var timer = request.StoreTimer;
			request.StoreTimer = null;

			if (timer != null)
			{
				timer.Cancel();
				timer.Dispose();
			}
		}

		private void SetStoreUi(bool open)
		{
			if (_storeUiOpen == open)
			{
				return;
			}

			_storeUiOpen = open;
			Raise(StoreUiChanged, open, nameof(StoreUiChanged));
		}

		private IapProductDefinition GetDefinition(string productId)
		{
			if (string.IsNullOrWhiteSpace(productId))
			{
				throw new ArgumentException("Product id is empty", nameof(productId));
			}

			if (!_definitions.TryGetValue(productId, out var definition))
			{
				throw new ArgumentException($"Product '{productId}' is not defined in IapServiceOptions.Products", nameof(productId));
			}

			return definition;
		}

		private IapProductDefinition FindDefinition(string productId)
		{
			if (productId == null)
			{
				return null;
			}

			if (_definitions.TryGetValue(productId, out var definition) || _definitionsByStoreId.TryGetValue(productId, out definition))
			{
				return definition;
			}

			return null;
		}

		private void ThrowIfUnusable()
		{
			if (_disposed)
			{
				throw new ObjectDisposedException(nameof(IapService));
			}

			ThrowIfWrongThread();
		}

		private void ThrowIfWrongThread()
		{
			if (!IsMainThread)
			{
				throw new InvalidOperationException("IapService must be used from the thread that created it");
			}
		}

		private void DestroyLifecycle()
		{
			var lifecycleObject = _lifecycleObject;
			_lifecycleObject = null;

			if (lifecycleObject == null)
			{
				return;
			}

			// 파괴는 프레임 끝에 일어나므로 먼저 끊어 늦게 오는 메시지가 닫힌 서비스에 닿지 않게 합니다
			if (lifecycleObject.TryGetComponent<IapLifecycle>(out var lifecycle))
			{
				lifecycle.Unbind();
			}

			if (Application.isPlaying)
			{
				Object.Destroy(lifecycleObject);
			}
			else
			{
				Object.DestroyImmediate(lifecycleObject);
			}
		}

		private static void Validate(IapServiceOptions options)
		{
			if (options.Products == null || options.Products.Count == 0)
			{
				throw new ArgumentException("IapServiceOptions.Products is empty", nameof(options));
			}

			var ids = new HashSet<string>();
			var storeIds = new HashSet<string>();

			foreach (var definition in options.Products)
			{
				if (definition == null)
				{
					throw new ArgumentException("IapServiceOptions.Products contains null", nameof(options));
				}

				if (!ids.Add(definition.Id))
				{
					throw new ArgumentException($"Product id '{definition.Id}' is defined twice", nameof(options));
				}

				if (!storeIds.Add(definition.StoreSpecificId))
				{
					throw new ArgumentException($"Store specific id '{definition.StoreSpecificId}' is used twice", nameof(options));
				}
			}

			RequirePositive(options.ConnectTimeout, nameof(options.ConnectTimeout));
			RequirePositive(options.FetchTimeout, nameof(options.FetchTimeout));
			RequirePositive(options.StoreResponseTimeout, nameof(options.StoreResponseTimeout));
			RequirePositive(options.FulfillTimeout, nameof(options.FulfillTimeout));
			RequirePositive(options.ConfirmTimeout, nameof(options.ConfirmTimeout));

			if (options.RetryDelay < IapServiceOptions.MinRetryDelay)
			{
				throw new ArgumentOutOfRangeException(nameof(options), options.RetryDelay, $"RetryDelay must be at least {IapServiceOptions.MinRetryDelay}");
			}

			if (options.MaxRetryDelay < options.RetryDelay)
			{
				throw new ArgumentOutOfRangeException(nameof(options), options.MaxRetryDelay, "MaxRetryDelay must not be less than RetryDelay");
			}
		}

		private static void RequirePositive(TimeSpan value, string name)
		{
			if (value <= TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(name, value, $"{name} must be positive");
			}
		}

		private static Func<double> CreateRandom()
		{
			var random = new System.Random();
			return random.NextDouble;
		}

		private static UniTask DefaultDelay(TimeSpan delay, CancellationToken cancellationToken)
		{
			return UniTask.Delay(delay, DelayType.Realtime, PlayerLoopTiming.Update, cancellationToken);
		}
	}
}
