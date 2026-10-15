// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>결제한 주문의 지급 요청입니다. 서버가 스토어에 검증한 뒤 영수증 원장과 같은 트랜잭션에서 지급합니다.</summary>
	[Preserve]
	public sealed class IapRedeemRequest
	{
		/// <summary><see cref="IapStoreNames"/> 의 이름(<c>IapOrder.StoreName</c>)</summary>
		[Preserve]
		public string Store { get; set; }

		/// <summary>게임 상품 ID(<c>IapOrder.ProductId</c>). 서버의 상품 목록에 있어야 합니다</summary>
		[Preserve]
		public string ProductId { get; set; }

		/// <summary>스토어 거래 ID(<c>IapOrder.TransactionId</c>, Google 은 구매 토큰). 원장의 유일 키입니다</summary>
		[Preserve]
		public string TransactionId { get; set; }

		/// <summary>스토어 영수증(<c>IapOrder.Receipt</c>). Google 은 서버가 구매 토큰으로 조회하므로 보지 않습니다</summary>
		[Preserve]
		public string Receipt { get; set; }

		/// <summary>거래 하나에 서명한 데이터(<c>IapOrder.SignedTransaction</c>, App Store StoreKit 2 JWS). App Store 는 이것으로 검증합니다</summary>
		[Preserve]
		public string SignedTransaction { get; set; }

		/// <summary>구매 때 넘긴 게임 데이터(<c>IapOrder.Payload</c>). 이전 실행의 결제를 복구하면 null 입니다</summary>
		[Preserve]
		public string Payload { get; set; }
	}
}
