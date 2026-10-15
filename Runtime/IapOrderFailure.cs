// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 주문 처리가 끝나지 못했다는 알림입니다(<see cref="IIapService.OrderFailed"/>). 원격 로그로 보내 지급 누락을 추적하세요.
	/// </summary>
	public sealed class IapOrderFailure
	{
		internal IapOrderFailure(IapOrder order, int code, string message, int attempts, bool willRetry)
		{
			Order = order;
			Code = code;
			Message = message;
			Attempts = attempts;
			WillRetry = willRetry;
		}

		public IapOrder Order { get; }

		/// <summary>
		/// <see cref="IapResultCodes.FulfillmentPending"/>(지급자가 나중에, 시간 초과, 예외), <see cref="IapResultCodes.FulfillmentRejected"/>,
		/// <see cref="IapResultCodes.ConfirmFailed"/>(지급은 됨), <see cref="IapResultCodes.UnknownProduct"/>
		/// </summary>
		public int Code { get; }

		public string Message { get; }

		/// <summary>이 주문의 연속 실패 수</summary>
		public int Attempts { get; }

		/// <summary>서비스가 백오프로 다시 시도하는지</summary>
		public bool WillRetry { get; }

		public override string ToString()
		{
			return $"{Order} failed with {Code} after {Attempts} attempt(s){(WillRetry ? ", will retry" : string.Empty)}: {Message}";
		}
	}
}
