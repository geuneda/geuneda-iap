// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>지급자 결과의 종류입니다.</summary>
	public enum FulfillmentStatus
	{
		/// <summary>이번에 지급했습니다. 서비스가 스토어 거래를 닫습니다</summary>
		Granted = 1,

		/// <summary>
		/// 이미 지급한 영수증입니다(중복 영수증). 서비스가 스토어 거래를 닫습니다. 다른 계정에 이미 지급한 영수증처럼
		/// 더 처리할 수 없지만 거래는 닫아야 하는 경우도 이것으로 돌려줍니다(레퍼런스는 중복·남의·오래된 영수증을 모두 닫음)
		/// </summary>
		AlreadyGranted = 2,

		/// <summary>지금은 처리할 수 없습니다(네트워크, 로그인 전, 서버 장애). 거래를 열어 두고 서비스가 백오프로 다시 시도합니다</summary>
		RetryLater = 3,

		/// <summary>
		/// 영수증이 가짜이거나 지급할 수 없습니다. 거래를 닫지 않고 이번 실행에서는 자동으로 다시 시도하지 않습니다
		/// (<see cref="IIapService.RecoverAsync"/>, 같은 상품 구매 시도, 다음 실행 때 다시 시도). Google 은 3일 안에 확정하지 않은 결제를 자동 환불합니다
		/// </summary>
		Rejected = 4,
	}

	/// <summary>지급자(<see cref="IPurchaseFulfiller"/>)의 결과입니다. <c>default</c> 는 정의되지 않은 값이라 서비스가 Error 로그 후 다시 시도합니다.</summary>
	public readonly struct FulfillmentResult
	{
		private FulfillmentResult(FulfillmentStatus status, string message, object response)
		{
			Status = status;
			Message = message;
			Response = response;
		}

		public FulfillmentStatus Status { get; }

		/// <summary>로그용 설명. 없으면 null</summary>
		public string Message { get; }

		/// <summary>호출부로 넘길 값(서버 응답, 지급한 보상 목록 등). 없으면 null</summary>
		public object Response { get; }

		public bool IsGranted => Status == FulfillmentStatus.Granted || Status == FulfillmentStatus.AlreadyGranted;

		public static FulfillmentResult Granted(object response = null)
		{
			return new FulfillmentResult(FulfillmentStatus.Granted, null, response);
		}

		public static FulfillmentResult AlreadyGranted(object response = null, string message = null)
		{
			return new FulfillmentResult(FulfillmentStatus.AlreadyGranted, message, response);
		}

		public static FulfillmentResult RetryLater(string message = null)
		{
			return new FulfillmentResult(FulfillmentStatus.RetryLater, message, null);
		}

		public static FulfillmentResult Rejected(string message = null)
		{
			return new FulfillmentResult(FulfillmentStatus.Rejected, message, null);
		}

		public override string ToString()
		{
			return Message == null ? Status.ToString() : $"{Status}: {Message}";
		}
	}
}
