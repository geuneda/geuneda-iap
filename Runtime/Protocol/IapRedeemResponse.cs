using System;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 지급 요청의 응답입니다. <typeparamref name="TGrant"/> 는 게임 서버의 지급 결과(보상 목록 등)입니다.
	/// 서버는 지급 결과를 원장에 함께 저장해 같은 계정의 중복 요청(<see cref="IapRedeemCodes.Duplicate"/>)에도 처음 응답을 돌려줍니다.
	/// </summary>
	[Preserve]
	public sealed class IapRedeemResponse<TGrant> : IIapRedeemResponse
	{
		[Preserve]
		public IapRedeemStatus Status { get; set; }

		/// <summary><see cref="IapRedeemCodes"/> 의 이유 코드. 지급했으면 <see cref="IapRedeemCodes.None"/></summary>
		[Preserve]
		public int Code { get; set; }

		/// <summary>개발자용 설명. 사용자에게 그대로 보여 주지 않습니다. 없으면 null</summary>
		[Preserve]
		public string Message { get; set; }

		/// <summary>게임 서버의 지급 결과. <see cref="IapRedeemStatus.Granted"/> 와 <see cref="IapRedeemCodes.Duplicate"/> 에만 있습니다</summary>
		[Preserve]
		public TGrant Grant { get; set; }

		/// <summary>지급한 게임 상품 ID</summary>
		[Preserve]
		public string ProductId { get; set; }

		[Preserve]
		public string TransactionId { get; set; }

		/// <summary>구매 수량(Google 여러 개 구매). 모르면 1</summary>
		[Preserve]
		public int Quantity { get; set; }

		/// <summary>테스트 결제(Google 라이선스 테스터, App Store 샌드박스, 가짜 스토어)인지. 매출에서 빼세요</summary>
		[Preserve]
		public bool IsTest { get; set; }

		/// <summary>스토어가 확인한, 수량을 포함한 총 실결제 금액(App Store 거래 서명, Google 주문 조회를 켰을 때). 모르면 null</summary>
		[Preserve]
		public decimal? PaidAmount { get; set; }

		/// <summary><see cref="PaidAmount"/> 의 ISO 4217 통화 코드. 모르면 null</summary>
		[Preserve]
		public string PaidCurrency { get; set; }

		/// <summary>구독 만료 시각(UTC). 구독이 아니면 null</summary>
		[Preserve]
		public DateTimeOffset? ExpiresAt { get; set; }
	}
}
