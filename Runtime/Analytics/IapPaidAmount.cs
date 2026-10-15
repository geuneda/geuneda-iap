// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Analytics
{
	/// <summary>매출로 기록할 실결제 금액과 그 통화입니다(<see cref="IapRevenueLogger"/>).</summary>
	public readonly struct IapPaidAmount
	{
		/// <param name="amount">수량을 포함한 총 실결제 금액(그 통화 단위). 매출 이벤트는 이것을 결제 서버가 확인한 수량으로 나눠 단가로 씁니다</param>
		/// <param name="currencyCode">ISO 4217 통화 코드. 서버가 확인한 거래 통화를 넘기세요(상품 조회 통화와 다를 수 있음)</param>
		public IapPaidAmount(decimal amount, string currencyCode)
		{
			Amount = amount;
			CurrencyCode = currencyCode;
		}

		public decimal Amount { get; }

		public string CurrencyCode { get; }

		public override string ToString()
		{
			return $"{Amount} {CurrencyCode}";
		}
	}
}
