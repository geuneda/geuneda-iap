using System;
using System.Threading;
using Cysharp.Threading.Tasks;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 호출자 한 명이 결과를 기다리는 자리입니다. 서비스가 결과를 넣거나, 호출자의 토큰이 취소되면 취소 값으로 끝납니다.
	/// </summary>
	/// <remarks>
	/// 취소 콜백 안에서 이어진 continuation 이 그 등록을 해제하면 UniTaskCompletionSource 의 잠금과 엇갈려 교착할 수 있습니다(servertime 에서 겪음).
	/// 그래서 등록은 서비스가 결과를 넣기 전에(<see cref="Complete"/>) 또는 아무도 결과를 넣지 않은 채 끝낼 때(<see cref="Release"/>)만 풉니다.
	/// </remarks>
	internal sealed class Waiter<T>
	{
		private readonly UniTaskCompletionSource<T> _source = new UniTaskCompletionSource<T>();
		private readonly CancellationTokenRegistration _registration;

		public Waiter(CancellationToken cancellationToken, T cancelledValue)
		{
			if (cancellationToken.CanBeCanceled)
			{
				_registration = cancellationToken.Register(() => _source.TrySetResult(cancelledValue));
			}
		}

		public UniTask<T> Task => _source.Task;

		public bool IsCompleted => _source.Task.Status != UniTaskStatus.Pending;

		/// <summary>결과를 넣습니다. 메인 스레드에서 부르고, 이미 끝났으면(취소 포함) 아무것도 하지 않습니다.</summary>
		public void Complete(T value)
		{
			if (IsCompleted)
			{
				return;
			}

			_registration.Dispose();
			_source.TrySetResult(value);
		}

		/// <summary>서비스 버그를 기다리는 호출자에게 예외로 넘깁니다(결과 코드로 숨기지 않음).</summary>
		public void Fail(Exception exception)
		{
			if (IsCompleted)
			{
				return;
			}

			_registration.Dispose();
			_source.TrySetException(exception);
		}

		/// <summary>결과를 넣지 않고 끝낼 때 취소 등록을 풉니다. 이미 끝났으면 아무것도 하지 않습니다.</summary>
		public void Release()
		{
			if (!IsCompleted)
			{
				_registration.Dispose();
			}
		}
	}
}
