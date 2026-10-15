// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 스토어가 구매·확정을 거절한 이유입니다. 어댑터는 SDK 의 값을 정수로 옮기지 말고 멤버마다 명시적으로 대응시킵니다
	/// (레퍼런스는 SDK 값을 정수로 넘기고 7 을 중복 거래로 비교했지만, Unity IAP v5 에서 7 은 검증 실패이고 중복 거래는 6 입니다).
	/// </summary>
	public enum IapStoreFailureReason
	{
		/// <summary>분류할 수 없는 실패</summary>
		Unknown = 0,

		/// <summary>스토어를 쓸 수 없음(연결 안 됨, 결제 기능 꺼짐, 미지원, 스토어 계정 로그인 안 됨)</summary>
		StoreUnavailable = 1,

		/// <summary>스토어에 진행 중인 다른 구매가 있음</summary>
		ExistingPurchasePending = 2,

		/// <summary>상품을 살 수 없음(스토어에 없거나 판매 중지)</summary>
		ProductUnavailable = 3,

		/// <summary>스토어 쪽 서명·검증 실패</summary>
		ValidationFailed = 4,

		/// <summary>사용자가 취소함</summary>
		UserCancelled = 5,

		/// <summary>결제 수단이 거절됨</summary>
		PaymentDeclined = 6,

		/// <summary>
		/// 같은 상품의 끝나지 않은 이전 거래가 있음(Google 의 ITEM_ALREADY_OWNED 등). 확정에서는 이미 확정된 거래라는 뜻입니다
		/// </summary>
		DuplicateTransaction = 7,
	}
}
