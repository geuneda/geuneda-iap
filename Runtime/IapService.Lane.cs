using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	// 주문을 하나씩 지급하고 확정하는 큐, 백오프, 바깥 코드 호출 보호
	public sealed partial class IapService
	{
		private readonly List<OrderEntry> _queue = new List<OrderEntry>();

		private bool _laneRunning;
		private CancellationTokenSource _wakeTimer;
		private TimeSpan _wakeAt;

		private void Enqueue(OrderEntry entry, bool front)
		{
			switch (entry.State)
			{
				case OrderState.Working:
					return;
				case OrderState.Queued:
					if (front && _queue.Remove(entry))
					{
						_queue.Insert(0, entry);
					}

					return;
			}

			entry.State = OrderState.Queued;

			if (front)
			{
				_queue.Insert(0, entry);
			}
			else
			{
				_queue.Add(entry);
			}
		}

		/// <summary>큐를 처리합니다. 초기화 전에는 상품 정보가 없어 기다립니다(초기화가 끝나면 복구가 다시 부름).</summary>
		private void RunLane()
		{
			if (_laneRunning || _disposed || !_initialized || _queue.Count == 0)
			{
				return;
			}

			RunLaneAsync().Forget();
		}

		private async UniTaskVoid RunLaneAsync()
		{
			_laneRunning = true;

			try
			{
				while (!_disposed && _queue.Count > 0)
				{
					var entry = _queue[0];
					_queue.RemoveAt(0);

					try
					{
						await ProcessAsync(entry);
					}
					catch (Exception exception)
					{
						// 서비스 버그입니다. 기다리는 호출자에게 예외로 넘기고, 거래는 열어 둔 채 백오프 뒤 다시 시도해 결제 처리가 멈추지 않게 합니다
						Logger.Error(exception, $"Processing {entry.Order} threw");

						if (!_disposed && _entries.ContainsKey(entry.TransactionId))
						{
							ScheduleRetry(entry);
						}

						FailWaiters(entry, exception);
					}
				}
			}
			finally
			{
				_laneRunning = false;
			}
		}

		private async UniTask ProcessAsync(OrderEntry entry)
		{
			entry.State = OrderState.Working;

			if (!entry.Granted)
			{
				// 시도마다 최신 영수증·서명과 상품 정보로 주문을 만듭니다(다시 조회한 주문, 초기화 전에 온 주문)
				entry.Order = CreateOrder(entry.Order.Definition, entry.StoreOrder, entry.Origin, entry.Payload);
				var order = entry.Order;
				var call = await CallAsync(token => _fulfiller.FulfillAsync(order, token), _fulfillTimeout, CancellationToken.None,
					$"Fulfilling {order}");

				if (_disposed)
				{
					return;
				}

				if (call.Outcome != CallOutcome.Completed)
				{
					Retry(entry, IapResultCodes.FulfillmentPending, call.Message);
					return;
				}

				var fulfillment = call.Value;

				switch (fulfillment.Status)
				{
					case FulfillmentStatus.Granted:
					case FulfillmentStatus.AlreadyGranted:
						entry.Granted = true;
						entry.Fulfillment = fulfillment;
						entry.Failures = 0;
						Logger.Info($"{order} fulfilled: {fulfillment}");
						break;
					case FulfillmentStatus.RetryLater:
						Retry(entry, IapResultCodes.FulfillmentPending, fulfillment.Message ?? "The fulfiller asked to retry later");
						return;
					case FulfillmentStatus.Rejected:
						Reject(entry, fulfillment.Message ?? "The fulfiller rejected the order");
						return;
					default:
						Logger.Error($"The fulfiller returned an undefined status {(int) fulfillment.Status} for {order}");
						Retry(entry, IapResultCodes.FulfillmentPending, $"Undefined fulfillment status {(int) fulfillment.Status}");
						return;
				}
			}

			await ConfirmAsync(entry);
		}

		private async UniTask ConfirmAsync(OrderEntry entry)
		{
			var storeOrder = entry.StoreOrder;
			var confirmedNow = false;
			var serverManaged = entry.Fulfillment.Response is IIapRedeemResponse;
			var closed = false;
			string failure = null;

			// 확정 재시도는 백오프로만 깨므로, 끊긴 스토어를 여기서 다시 연결합니다(SDK 의 재연결 시도는 횟수가 정해져 있음)
			var connected = await EnsureConnectedAsync();

			if (_disposed)
			{
				return;
			}

			if (!connected.IsSuccess)
			{
				failure = $"The store is not connected: {connected}";
			}
			else
			{
				var call = await CallAsync(token => _store.ConfirmAsync(storeOrder, token), _confirmTimeout, CancellationToken.None,
					$"Confirming {entry.Order}");

				if (_disposed)
				{
					return;
				}

				if (call.Outcome != CallOutcome.Completed)
				{
					failure = call.Message;
				}
				else
				{
					switch (call.Value.Status)
					{
						case IapConfirmStatus.Confirmed:
							confirmedNow = true;
							closed = true;
							break;
						case IapConfirmStatus.AlreadyConfirmed:
							closed = true;

							if (serverManaged)
							{
								// 결제 서버가 지급하면서 먼저 확인한 결제(Google acknowledge)
								Logger.Info($"{entry.Order} was already acknowledged by the server: {call.Value.Message}");
							}
							else
							{
								Logger.Warning($"{entry.Order} was already closed in the store: {call.Value.Message}");
							}

							break;
						default:
							failure = $"The store did not confirm: {call.Value.Reason} {call.Value.Message}";
							break;
					}
				}
			}

			if (closed)
			{
				_entries.Remove(entry.TransactionId);
				_closed.Add(entry.TransactionId);
				entry.Failures = 0;
				Logger.Info($"{entry.Order} closed");
			}
			else
			{
				// 지급은 끝났습니다. 지급자를 다시 부르지 않고 확정만 다시 시도합니다
				ScheduleRetry(entry);
				Logger.Warning($"{entry.Order} was fulfilled but not confirmed ({failure}); retrying the confirmation in {entry.RetryAt - _clock.Elapsed}");
				Raise(OrderFailed, new IapOrderFailure(entry.Order, IapResultCodes.ConfirmFailed, failure, entry.Failures, true), nameof(OrderFailed));
			}

			var settlement = new PurchaseSettlement(entry.Order, entry.Fulfillment, closed,
				entry.Purchase != null && !entry.Purchase.Waiter.IsCompleted);

			// 매출 지점은 거래마다 한 번이어야 하므로 근거를 하나만 쓴다. 결제 서버 응답이면 원장의 첫 지급(Granted, 거래당 한 번),
			// 그 밖은 스토어의 첫 확정(Confirmed)이다. 둘을 섞으면 여러 기기가 같은 거래를 처리할 때 두 번 잡힌다(2026-10-15 리뷰 2회차)
			var revenuePoint = closed && (serverManaged ? entry.Fulfillment.Status == FulfillmentStatus.Granted : confirmedNow);

			if (revenuePoint)
			{
				Raise(PurchaseConfirmed, settlement, nameof(PurchaseConfirmed));
			}

			if (!entry.Announced)
			{
				entry.Announced = true;
				Raise(OrderSettled, settlement, nameof(OrderSettled));
			}

			Finish(entry, Result<PurchaseSettlement>.Ok(settlement));
		}

		private void Retry(OrderEntry entry, int code, string message)
		{
			ScheduleRetry(entry);
			Logger.Warning($"{entry.Order} is not fulfilled yet ({message}); retrying in {entry.RetryAt - _clock.Elapsed}");
			Raise(OrderFailed, new IapOrderFailure(entry.Order, code, message, entry.Failures, true), nameof(OrderFailed));
			Finish(entry, Result<PurchaseSettlement>.Fail(code, message));
		}

		private void Reject(OrderEntry entry, string message)
		{
			entry.State = OrderState.Rejected;
			entry.Failures++;
			Logger.Warning($"{entry.Order} was rejected by the fulfiller ({message}); the store transaction stays open");
			Raise(OrderFailed, new IapOrderFailure(entry.Order, IapResultCodes.FulfillmentRejected, message, entry.Failures, false), nameof(OrderFailed));
			Finish(entry, Result<PurchaseSettlement>.Fail(IapResultCodes.FulfillmentRejected, message));
		}

		private static void Finish(OrderEntry entry, Result<PurchaseSettlement> result)
		{
			var waiters = entry.Waiters.ToArray();
			entry.Waiters.Clear();

			foreach (var waiter in waiters)
			{
				waiter.Complete(result);
			}
		}

		private static void FailWaiters(OrderEntry entry, Exception exception)
		{
			var waiters = entry.Waiters.ToArray();
			entry.Waiters.Clear();

			foreach (var waiter in waiters)
			{
				waiter.Fail(exception);
			}
		}

		private void ScheduleRetry(OrderEntry entry)
		{
			entry.Failures++;
			entry.RetryAt = _clock.Elapsed + RetryDelayFor(entry.Failures);
			entry.State = OrderState.Waiting;
			ScheduleWake();
		}

		/// <summary>실패 수에 따른 대기 시간입니다. 두 배씩 상한까지 늘리고 50~100% 무작위로 줄여 여러 기기가 한꺼번에 몰리지 않게 합니다.</summary>
		private TimeSpan RetryDelayFor(int failures)
		{
			var ticks = _retryDelay.Ticks;

			for (var i = 1; i < failures && ticks < _maxRetryDelay.Ticks; i++)
			{
				ticks *= 2;
			}

			ticks = Math.Min(ticks, _maxRetryDelay.Ticks);
			return TimeSpan.FromTicks((long) (ticks * (0.5 + 0.5 * _random())));
		}

		/// <summary>백오프 중인 주문 가운데 가장 이른 시각에 깨우기를 하나 둡니다. 더 이른 것이 생기면 바꿉니다.</summary>
		private void ScheduleWake()
		{
			if (_disposed)
			{
				return;
			}

			var earliest = TimeSpan.MaxValue;

			foreach (var entry in _entries.Values)
			{
				if (entry.State == OrderState.Waiting && entry.RetryAt < earliest)
				{
					earliest = entry.RetryAt;
				}
			}

			if (earliest == TimeSpan.MaxValue)
			{
				CancelWake();
				return;
			}

			if (_wakeTimer != null && _wakeAt <= earliest)
			{
				return;
			}

			CancelWake();
			var timer = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
			_wakeTimer = timer;
			_wakeAt = earliest;
			var delay = earliest - _clock.Elapsed;
			WakeAsync(timer, delay < TimeSpan.Zero ? TimeSpan.Zero : delay).Forget();
		}

		private async UniTaskVoid WakeAsync(CancellationTokenSource timer, TimeSpan delay)
		{
			try
			{
				await _delay(delay, timer.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			if (_disposed || _wakeTimer != timer)
			{
				return;
			}

			_wakeTimer = null;
			timer.Dispose();
			var now = _clock.Elapsed;

			foreach (var entry in _entries.Values)
			{
				if (entry.State == OrderState.Waiting && entry.RetryAt <= now)
				{
					Enqueue(entry, false);
				}
			}

			RunLane();
			ScheduleWake();
		}

		private void CancelWake()
		{
			var timer = _wakeTimer;
			_wakeTimer = null;

			if (timer != null)
			{
				timer.Cancel();
				timer.Dispose();
			}
		}

		/// <summary>
		/// 지급자·스토어를 제한 시간을 걸고 부릅니다. 예외·시간 초과·취소를 값으로 바꾸고 메인 스레드로 돌아옵니다.
		/// 시간이 지나면 호출에 취소를 알리고 기다리지 않습니다(호출은 뒤에서 끝날 수 있음).
		/// </summary>
		private async UniTask<CallResult<T>> CallAsync<T>(Func<CancellationToken, UniTask<T>> operation, TimeSpan timeout, CancellationToken external,
			string description)
		{
			var call = external.CanBeCanceled
				? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, external)
				: CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
			var timer = external.CanBeCanceled
				? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, external)
				: CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
			var (finished, result) = await UniTask.WhenAny(InvokeAsync(operation, call, description), TimerAsync(timeout, timer.Token));

			if (!IsMainThread)
			{
				await UniTask.SwitchToMainThread();
			}

			timer.Cancel();
			timer.Dispose();

			if (finished)
			{
				return result;
			}

			var cancelled = _lifetime.IsCancellationRequested || external.IsCancellationRequested;
			// 호출이 아직 토큰을 쓸 수 있어 해제는 호출이 끝날 때(InvokeAsync) 합니다
			CancelQuietly(call, description);

			return cancelled
				? new CallResult<T>(CallOutcome.Cancelled, default, $"{description} was cancelled")
				: new CallResult<T>(CallOutcome.TimedOut, default, $"{description} did not finish within {timeout}");
		}

		/// <summary>호출을 끝까지 지켜봅니다. 시간 초과로 버려진 호출도 끝나면 토큰 소스를 해제해 서비스 수명 토큰의 등록이 쌓이지 않게 합니다.</summary>
		private static async UniTask<CallResult<T>> InvokeAsync<T>(Func<CancellationToken, UniTask<T>> operation, CancellationTokenSource source,
			string description)
		{
			var token = source.Token;

			try
			{
				var value = await operation(token);
				return new CallResult<T>(CallOutcome.Completed, value, null);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				return new CallResult<T>(CallOutcome.Cancelled, default, $"{description} was cancelled");
			}
			catch (Exception exception)
			{
				// 바깥 코드(지급자·어댑터)의 예외는 로그로 드러내고 거래를 열어 둡니다. 다른 스레드일 수 있어 예외만 씁니다
				Logger.Error(exception, $"{description} threw");
				return new CallResult<T>(CallOutcome.Threw, default, $"{description} threw {exception.GetType().Name}: {exception.Message}");
			}
			finally
			{
				source.Dispose();
			}
		}

		private async UniTask TimerAsync(TimeSpan timeout, CancellationToken token)
		{
			try
			{
				await _delay(timeout, token);
			}
			catch (OperationCanceledException)
			{
				// 호출이 먼저 끝났거나 취소됨
			}
		}

		private static Result ToResult(CallResult<Result> call)
		{
			switch (call.Outcome)
			{
				case CallOutcome.Completed:
					return call.Value;
				case CallOutcome.TimedOut:
					return Result.Fail(ResultCodes.Timeout, call.Message);
				case CallOutcome.Cancelled:
					return Result.Fail(ResultCodes.Cancelled, call.Message);
				default:
					return Result.Fail(IapResultCodes.StoreError, call.Message);
			}
		}

		private static Result<T> ToResult<T>(CallResult<Result<T>> call)
		{
			switch (call.Outcome)
			{
				case CallOutcome.Completed:
					return call.Value;
				case CallOutcome.TimedOut:
					return Result<T>.Fail(ResultCodes.Timeout, call.Message);
				case CallOutcome.Cancelled:
					return Result<T>.Fail(ResultCodes.Cancelled, call.Message);
				default:
					return Result<T>.Fail(IapResultCodes.StoreError, call.Message);
			}
		}

		private static void CancelQuietly(CancellationTokenSource source, string description)
		{
			try
			{
				source.Cancel();
			}
			catch (AggregateException exception)
			{
				// 바깥 코드가 토큰에 건 콜백의 예외입니다. 다른 정리가 멈추지 않게 로그만 남깁니다
				Logger.Error(exception, $"Cancellation callbacks of {description} threw");
			}
			catch (ObjectDisposedException)
			{
				// 시간 초과와 거의 같은 때에 호출이 끝나 이미 해제했습니다. 취소할 것이 없습니다
			}
		}

		private static void Raise(Action handlers, string name)
		{
			if (handlers == null)
			{
				return;
			}

			foreach (var handler in handlers.GetInvocationList())
			{
				try
				{
					((Action) handler)();
				}
				catch (Exception exception)
				{
					// 게임 처리기의 예외가 지급·확정 흐름을 끊지 않게 로그로 드러내고 계속합니다
					Logger.Error(exception, $"A {name} handler threw");
				}
			}
		}

		private static void Raise<T>(Action<T> handlers, T argument, string name)
		{
			if (handlers == null)
			{
				return;
			}

			foreach (var handler in handlers.GetInvocationList())
			{
				try
				{
					((Action<T>) handler)(argument);
				}
				catch (Exception exception)
				{
					Logger.Error(exception, $"A {name} handler threw");
				}
			}
		}
	}
}
