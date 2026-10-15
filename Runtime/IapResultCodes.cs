using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 결제 결과 코드입니다(7000 블록). 공용 코드 <see cref="ResultCodes.Cancelled"/>(호출자 취소), <see cref="ResultCodes.Timeout"/>
	/// (스토어가 제한 시간 안에 답하지 않음), <see cref="ResultCodes.InProgress"/>(다른 구매·복원이 진행 중)도 씁니다.
	/// </summary>
	/// <remarks>
	/// 결제가 됐을 수 있는 코드(<see cref="ResultCodes.Timeout"/>, <see cref="ResultCodes.Cancelled"/>, <see cref="Deferred"/>,
	/// <see cref="DuplicateTransaction"/>, <see cref="FulfillmentPending"/>)에서는 '결제 실패'라고 보여 주지 마세요. 늦게 온 결제는 서비스가 뒤에서 지급하고
	/// <see cref="IIapService.OrderSettled"/> 로 알립니다.
	/// </remarks>
	public static class IapResultCodes
	{
		/// <summary><see cref="IIapService.InitializeAsync"/> 가 아직 성공하지 않음</summary>
		public const int NotInitialized = 7001;

		/// <summary>스토어에 연결할 수 없거나 결제 기능을 쓸 수 없음</summary>
		public const int StoreUnavailable = 7002;

		/// <summary>정의한 상품이지만 스토어에서 받지 못함(스토어에 없거나 이 지역에서 팔지 않음)</summary>
		public const int ProductNotFound = 7003;

		/// <summary>스토어가 지금 팔 수 없는 상품이라고 함</summary>
		public const int ProductUnavailable = 7004;

		/// <summary>사용자가 결제창을 닫음. 실패 안내를 띄우지 않습니다</summary>
		public const int UserCancelled = 7005;

		/// <summary>결제가 승인 대기로 넘어감(자녀 보호 승인, 현장 결제 등). 승인되면 뒤에서 지급합니다</summary>
		public const int Deferred = 7006;

		/// <summary>결제 수단이 거절됨</summary>
		public const int PaymentDeclined = 7007;

		/// <summary>같은 상품의 끝나지 않은 이전 거래가 있는데 다시 조회해도 찾지 못함. 서비스가 뒤에서 계속 복구합니다</summary>
		public const int DuplicateTransaction = 7008;

		/// <summary>결제는 됐지만 지급자가 나중에를 돌려줬거나 시간 초과·예외가 남. 서비스가 백오프로 다시 지급합니다</summary>
		public const int FulfillmentPending = 7009;

		/// <summary>지급자가 영수증을 거절함</summary>
		public const int FulfillmentRejected = 7010;

		/// <summary>스토어 쪽 서명·검증 실패</summary>
		public const int StoreValidationFailed = 7011;

		/// <summary>스토어에 진행 중인 다른 구매가 있음</summary>
		public const int ExistingPurchasePending = 7012;

		/// <summary>스토어가 분류할 수 없는 오류를 주거나 어댑터가 예외를 던짐</summary>
		public const int StoreError = 7013;

		/// <summary>초기화 때 스토어에서 상품을 하나도 받지 못함</summary>
		public const int NoProducts = 7014;

		/// <summary>스토어나 정의가 모르는 상품의 미확정 주문. 지급·확정하지 않습니다(<see cref="IIapService.OrderFailed"/> 로만 알림)</summary>
		public const int UnknownProduct = 7015;

		/// <summary>지급은 끝났고 스토어 확정만 실패함. 서비스가 확정만 다시 시도합니다(<see cref="IIapService.OrderFailed"/> 로만 알림)</summary>
		public const int ConfirmFailed = 7016;
	}
}
