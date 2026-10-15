namespace Geuneda.Iap.Server
{
	/// <summary>지급자(<see cref="IPurchaseGrantor"/>)에 넘기는 지급 하나입니다.</summary>
	public sealed class PurchaseGrant
	{
		internal PurchaseGrant(string accountId, IapServerProduct product, VerifiedPurchase purchase, string payload, int quantity)
		{
			AccountId = accountId;
			Product = product;
			Purchase = purchase;
			Payload = payload;
			Quantity = quantity;
		}

		/// <summary>지급받을 계정</summary>
		public string AccountId { get; }

		public IapServerProduct Product { get; }

		/// <summary>스토어가 확인한 결제(수량, 테스트 여부, 구독 만료 시각 등)</summary>
		public VerifiedPurchase Purchase { get; }

		/// <summary>
		/// 지급할 수량입니다. 보통 <see cref="VerifiedPurchase.Quantity"/> 이고, 지급 요청 전에 일부가 환불됐으면(서버 알림이 먼저 옴) 그만큼 적습니다. 이 값만큼 지급하세요
		/// </summary>
		public int Quantity { get; }

		/// <summary>클라이언트가 구매 때 넘긴 게임 데이터(고른 보상 등). 이전 실행의 결제를 복구하면 null 이고, 클라이언트가 보낸 값이라 검사해서 씁니다</summary>
		public string Payload { get; }
	}
}
