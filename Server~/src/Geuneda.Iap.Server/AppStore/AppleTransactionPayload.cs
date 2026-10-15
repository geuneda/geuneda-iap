namespace Geuneda.Iap.Server
{
	/// <summary>
	/// App Store 거래 서명(JWSTransactionDecodedPayload) 중 쓰는 필드입니다. 시각은 Unix 밀리초, <c>price</c> 는 통화 단위의 1000분의 1(milliunits)입니다.
	/// 이름은 JSON camelCase 와 같습니다(IapServerJson 의 Web 기본값).
	/// </summary>
	internal sealed class AppleTransactionPayload
	{
		public string TransactionId { get; set; }
		public string OriginalTransactionId { get; set; }
		public string BundleId { get; set; }
		public string ProductId { get; set; }
		public long? PurchaseDate { get; set; }
		public int? Quantity { get; set; }
		public string Type { get; set; }
		public string Environment { get; set; }
		public string AppAccountToken { get; set; }
		public long? RevocationDate { get; set; }
		public int? RevocationReason { get; set; }
		public long? ExpiresDate { get; set; }
		public long? Price { get; set; }
		public string Currency { get; set; }
	}

	/// <summary>App Store Server Notifications V2 요청 본문</summary>
	internal sealed class AppleNotificationRequest
	{
		public string SignedPayload { get; set; }
	}

	/// <summary>알림 페이로드(ResponseBodyV2DecodedPayload) 중 쓰는 필드</summary>
	internal sealed class AppleNotificationPayload
	{
		public string NotificationType { get; set; }
		public string Subtype { get; set; }

		/// <summary>JSON 이름은 <c>notificationUUID</c>(대소문자 무시로 읽음)</summary>
		public string NotificationUuid { get; set; }

		public long? SignedDate { get; set; }
		public AppleNotificationData Data { get; set; }
	}

	internal sealed class AppleNotificationData
	{
		public long? AppAppleId { get; set; }
		public string BundleId { get; set; }
		public string Environment { get; set; }
		public string SignedTransactionInfo { get; set; }
		public string SignedRenewalInfo { get; set; }

		/// <summary>자동 갱신 구독 상태: 1 이용 중, 2 만료, 3 결제 재시도, 4 결제 유예, 5 철회</summary>
		public int? Status { get; set; }
	}

	/// <summary>갱신 정보(JWSRenewalInfoDecodedPayload) 중 쓰는 필드</summary>
	internal sealed class AppleRenewalPayload
	{
		public string OriginalTransactionId { get; set; }
		public string ProductId { get; set; }

		/// <summary>1 자동 갱신 켜짐, 0 꺼짐</summary>
		public int? AutoRenewStatus { get; set; }

		public bool? IsInBillingRetryPeriod { get; set; }
		public long? GracePeriodExpiresDate { get; set; }
	}
}
