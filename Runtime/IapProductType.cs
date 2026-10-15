// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>스토어 상품 종류입니다. 스토어가 확정(Confirm)을 어떻게 처리할지 이것으로 정합니다.</summary>
	public enum IapProductType
	{
		/// <summary>여러 번 살 수 있고, 확정하면 소모되는 상품(재화 등)</summary>
		Consumable = 1,

		/// <summary>한 번 사면 계속 보유하는 상품(광고 제거 등). 다른 기기에서 복원할 수 있습니다</summary>
		NonConsumable = 2,

		/// <summary>기간마다 갱신되는 구독. 만료·갱신 판정은 서버가 합니다</summary>
		Subscription = 3,
	}
}
