namespace Geuneda.Iap.Server
{
	/// <summary>지급자 결과의 종류입니다.</summary>
	public enum GrantStatus
	{
		/// <summary>지급했습니다. 원장에 기록하고 커밋합니다</summary>
		Granted = 1,

		/// <summary>지급할 수 없습니다(구매 제한 등). 기록하지 않고 클라이언트는 거래를 열어 둡니다(<see cref="IapRedeemCodes.GrantRejected"/>)</summary>
		Rejected = 2,

		/// <summary>지금은 지급할 수 없습니다. 기록하지 않고 클라이언트가 다시 보냅니다(<see cref="IapRedeemCodes.GrantDeferred"/>)</summary>
		RetryLater = 3,
	}

	/// <summary>지급자(<see cref="IPurchaseGrantor"/>)의 결과입니다. <c>default</c> 는 정의되지 않은 값이라 지급 요청이 예외로 끝납니다.</summary>
	public readonly struct GrantResult
	{
		private GrantResult(GrantStatus status, object response, string message)
		{
			Status = status;
			Response = response;
			Message = message;
		}

		public GrantStatus Status { get; }

		/// <summary>
		/// 클라이언트에 돌려줄 지급 결과(보상 목록 등, <see cref="IapRedeemResponse{TGrant}.Grant"/>). JSON 으로 원장에 함께 저장해 같은 계정의 중복 요청에도 돌려줍니다. 없으면 null
		/// </summary>
		public object Response { get; }

		/// <summary>로그·응답용 설명. 없으면 null</summary>
		public string Message { get; }

		public static GrantResult Granted(object response = null)
		{
			return new GrantResult(GrantStatus.Granted, response, null);
		}

		public static GrantResult Rejected(string message = null)
		{
			return new GrantResult(GrantStatus.Rejected, null, message);
		}

		public static GrantResult RetryLater(string message = null)
		{
			return new GrantResult(GrantStatus.RetryLater, null, message);
		}

		public override string ToString()
		{
			return Message == null ? Status.ToString() : $"{Status}: {Message}";
		}
	}
}
