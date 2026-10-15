// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 지급이 끝난 주문입니다. <see cref="IIapService.PurchaseAsync"/> 의 성공 값이고, 주문마다 한 번
	/// <see cref="IIapService.OrderSettled"/> 로도 옵니다.
	/// </summary>
	public sealed class PurchaseSettlement
	{
		internal PurchaseSettlement(IapOrder order, FulfillmentResult fulfillment, bool isConfirmed, bool isAwaited)
		{
			Order = order;
			Fulfillment = fulfillment;
			IsConfirmed = isConfirmed;
			IsAwaited = isAwaited;
		}

		public IapOrder Order { get; }

		/// <summary>지급자의 결과(<see cref="FulfillmentStatus.Granted"/> 나 <see cref="FulfillmentStatus.AlreadyGranted"/>)</summary>
		public FulfillmentResult Fulfillment { get; }

		/// <summary>
		/// 스토어 거래가 닫혔는지입니다. false 면 지급은 끝났고 확정만 실패한 것이라 서비스가 확정만 다시 시도합니다.
		/// 사용자에게는 지급 완료로 보여 주세요
		/// </summary>
		public bool IsConfirmed { get; }

		/// <summary>
		/// <see cref="IIapService.PurchaseAsync"/> 호출이 이 결과를 받는지입니다. 보상 화면을 구매 결과와 이벤트에서 두 번 띄우지 않으려면
		/// 이벤트에서는 false 인 것만 처리하세요
		/// </summary>
		public bool IsAwaited { get; }
	}
}
