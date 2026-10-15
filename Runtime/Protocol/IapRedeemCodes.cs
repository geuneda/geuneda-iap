// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 결제 서버가 지급 응답(<see cref="IapRedeemResponse{TGrant}.Code"/>)으로 돌려주는 이유 코드입니다(iap 7000 블록의 7100~7199). 서버(<c>Server~</c>)와 함께 컴파일합니다.
	/// 지급했으면 <see cref="None"/> 입니다.
	/// </summary>
	public static class IapRedeemCodes
	{
		/// <summary>이번에 지급함(<see cref="IapRedeemStatus.Granted"/>)</summary>
		public const int None = 0;

		/// <summary>이 서버 코드 범위의 시작입니다. 코드로는 쓰지 않습니다.</summary>
		public const int RangeStart = 7100;

		/// <summary>이 계정에 이미 지급한 거래. 처음 지급의 응답을 다시 돌려줍니다(<see cref="IapRedeemStatus.AlreadyGranted"/>)</summary>
		public const int Duplicate = RangeStart + 1;

		/// <summary>다른 계정에 지급한 거래. 지급하지 않고 거래를 닫습니다(<see cref="IapRedeemStatus.AlreadyGranted"/>, 레퍼런스의 '남의 영수증')</summary>
		public const int OwnedByOtherAccount = RangeStart + 2;

		/// <summary>환불·취소된 거래이거나 결제 전에 취소된 구독. 지급하지 않고 거래를 닫습니다(<see cref="IapRedeemStatus.AlreadyGranted"/>)</summary>
		public const int Revoked = RangeStart + 3;

		/// <summary>서명·형식이 잘못됐거나 스토어가 모르는 거래(<see cref="IapRedeemStatus.Rejected"/>)</summary>
		public const int InvalidReceipt = RangeStart + 4;

		/// <summary>다른 앱(번들 ID, 패키지 이름)의 거래(<see cref="IapRedeemStatus.Rejected"/>)</summary>
		public const int AppMismatch = RangeStart + 5;

		/// <summary>서버 상품 목록에 없는 상품이거나, 거래의 스토어 상품이 요청한 상품과 다름(<see cref="IapRedeemStatus.Rejected"/>)</summary>
		public const int ProductMismatch = RangeStart + 6;

		/// <summary>서버에 검증기가 없는 스토어(<see cref="IapRedeemStatus.Rejected"/>)</summary>
		public const int UnsupportedStore = RangeStart + 7;

		/// <summary>허용하지 않은 환경의 거래(샌드박스, 테스트 결제, Xcode 로컬 테스트)(<see cref="IapRedeemStatus.Rejected"/>)</summary>
		public const int EnvironmentNotAllowed = RangeStart + 8;

		/// <summary>결제가 아직 끝나지 않음(Google 현장 결제 등)(<see cref="IapRedeemStatus.RetryLater"/>)</summary>
		public const int PaymentPending = RangeStart + 9;

		/// <summary>스토어 검증 API 를 쓸 수 없음(네트워크, 할당량, 서버 자격 증명)(<see cref="IapRedeemStatus.RetryLater"/>)</summary>
		public const int StoreUnavailable = RangeStart + 10;

		/// <summary>게임이 지급을 거절함(<see cref="IapRedeemStatus.Rejected"/>)</summary>
		public const int GrantRejected = RangeStart + 11;

		/// <summary>게임이 지금은 지급할 수 없다고 함(<see cref="IapRedeemStatus.RetryLater"/>)</summary>
		public const int GrantDeferred = RangeStart + 12;

		/// <summary>요청 형식이 잘못됨. HTTP 400(<see cref="IapRedeemStatus.Rejected"/>)</summary>
		public const int BadRequest = RangeStart + 13;

		/// <summary>계정을 확인하지 못함. HTTP 401(<see cref="IapRedeemStatus.RetryLater"/>)</summary>
		public const int Unauthorized = RangeStart + 14;

		/// <summary>이 서버 코드 범위의 끝입니다. 코드로는 쓰지 않습니다.</summary>
		public const int RangeEnd = 7199;
	}
}
