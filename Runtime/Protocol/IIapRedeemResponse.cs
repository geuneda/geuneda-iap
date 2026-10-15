// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 지급 결과 형식과 관계없이 지급 응답(<see cref="IapRedeemResponse{TGrant}"/>)을 읽는 인터페이스입니다. 매출 기본 규칙(<c>IapRevenueLogger.DefaultPaidAmount</c>)이
	/// 지급 결과(<c>FulfillmentResult.Response</c>)가 이 응답이면 테스트 결제·환불을 빼고 서버가 확인한 금액을 씁니다.
	/// </summary>
	public interface IIapRedeemResponse
	{
		IapRedeemStatus Status { get; }

		/// <summary><see cref="IapRedeemCodes"/> 의 이유 코드</summary>
		int Code { get; }

		string ProductId { get; }

		string TransactionId { get; }

		/// <summary>스토어가 확인한 구매 수량. 모르면 1(0 도 1 로 봄)</summary>
		int Quantity { get; }

		bool IsTest { get; }

		/// <summary>수량을 포함한 총 실결제 금액(App Store 거래 서명의 <c>price</c>, Google 주문의 <c>total</c>). 모르면 null</summary>
		decimal? PaidAmount { get; }

		string PaidCurrency { get; }
	}
}
