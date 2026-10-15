namespace Geuneda.Iap.Server
{
	/// <summary>서버 알림 하나를 처리한 결과입니다. HTTP 응답을 정합니다.</summary>
	internal enum NotificationOutcome
	{
		/// <summary>반영함(200)</summary>
		Processed = 1,

		/// <summary>할 일이 없음(테스트 알림, 다른 앱, 이 서버가 지급하지 않은 거래)(200)</summary>
		Ignored = 2,

		/// <summary>지금은 처리할 수 없음(스토어 API 장애). 스토어가 다시 보냄(503)</summary>
		RetryLater = 3,

		/// <summary>서명·토큰을 검증하지 못함(401)</summary>
		Unauthorized = 4,

		/// <summary>본문을 해석하지 못함. 버그를 고친 뒤 다시 받을 수 있게 거절함(400)</summary>
		BadRequest = 5,
	}
}
