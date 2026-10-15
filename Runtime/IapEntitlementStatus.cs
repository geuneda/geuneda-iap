// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 스토어가 보는 상품 보유 상태입니다(<see cref="IIapService.CheckEntitlementAsync"/>). 기기의 스토어 계정 기준이라
	/// 게임 계정의 권한은 서버로 판정하세요.
	/// </summary>
	public enum IapEntitlementStatus
	{
		/// <summary>판정할 수 없음(조회 실패 등)</summary>
		Unknown = 0,

		/// <summary>보유하지 않음</summary>
		NotEntitled = 1,

		/// <summary>소모성 상품을 샀지만 아직 확정(소비)하지 않음</summary>
		EntitledUntilConsumed = 2,

		/// <summary>샀지만 아직 확정하지 않음</summary>
		EntitledButNotFinished = 3,

		/// <summary>보유 중(확정한 비소모성, 유효한 구독)</summary>
		FullyEntitled = 4,
	}
}
