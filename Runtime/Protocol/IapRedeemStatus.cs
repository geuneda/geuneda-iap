// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 지급 요청의 결과입니다. 값은 클라이언트 지급 결과(<c>FulfillmentStatus</c>)와 같고, 클라이언트는 이것으로 스토어 거래를 닫을지 정합니다.
	/// </summary>
	public enum IapRedeemStatus
	{
		/// <summary>이번에 지급했습니다. 거래를 닫습니다</summary>
		Granted = 1,

		/// <summary>더 지급할 것이 없습니다(이미 지급, 다른 계정에 지급, 환불·취소). 거래를 닫습니다. 이유는 <see cref="IapRedeemResponse{TGrant}.Code"/></summary>
		AlreadyGranted = 2,

		/// <summary>지금은 처리할 수 없습니다(결제 대기, 스토어 장애, 게임이 나중에). 거래를 열어 두고 다시 보냅니다</summary>
		RetryLater = 3,

		/// <summary>지급할 수 없는 영수증입니다(위조, 다른 앱, 상품 불일치). 거래를 열어 둡니다</summary>
		Rejected = 4,
	}
}
