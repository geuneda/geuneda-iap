// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	internal enum CallOutcome
	{
		/// <summary>제한 시간 안에 값을 돌려줌</summary>
		Completed,

		/// <summary>제한 시간이 지남(호출은 뒤에서 계속될 수 있음)</summary>
		TimedOut,

		/// <summary>서비스 종료나 호출자 취소</summary>
		Cancelled,

		/// <summary>예외를 던짐(이미 Error 로그를 남김)</summary>
		Threw,
	}

	/// <summary>지급자·스토어 호출 한 번의 결과입니다. 예외와 시간 초과를 값으로 바꿔 큐가 멈추지 않게 합니다.</summary>
	internal readonly struct CallResult<T>
	{
		public CallResult(CallOutcome outcome, T value, string message)
		{
			Outcome = outcome;
			Value = value;
			Message = message;
		}

		public CallOutcome Outcome { get; }

		public T Value { get; }

		public string Message { get; }
	}
}
