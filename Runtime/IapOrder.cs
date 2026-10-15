// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>주문이 서비스에 들어온 경로입니다.</summary>
	public enum IapOrderOrigin
	{
		/// <summary>이번 실행의 <see cref="IIapService.PurchaseAsync"/> 로 연 결제(시간 초과·승인 대기 뒤 늦게 온 결과 포함)</summary>
		Purchase = 1,

		/// <summary>스토어에 남아 있던 미확정 주문(이전 실행의 결제, 스토어가 다시 보낸 거래, 구독 갱신 등)</summary>
		Recovered = 2,

		/// <summary><see cref="IIapService.RestoreAsync"/> 가 돌려준 보유 주문(이미 확정됨)</summary>
		Restored = 3,
	}

	/// <summary>
	/// 지급자(<see cref="IPurchaseFulfiller"/>)와 게임에 넘기는 주문입니다. 서버 지급 요청에 필요한 영수증과 거래 ID,
	/// 매출 기록에 쓸 가격 정보를 담습니다.
	/// </summary>
	public sealed class IapOrder
	{
		internal IapOrder(IapProductDefinition definition, string productId, IapProduct product, IapStoreOrder storeOrder, string storeName,
			IapOrderOrigin origin, string payload)
		{
			Definition = definition;
			ProductId = definition?.Id ?? productId;
			StoreName = storeName;
			TransactionId = storeOrder.TransactionId;
			Receipt = storeOrder.Receipt;
			SignedTransaction = storeOrder.SignedTransaction;
			OriginalTransactionId = storeOrder.OriginalTransactionId;
			Origin = origin;
			Payload = payload;
			Price = product?.Price ?? 0m;
			CurrencyCode = product?.CurrencyCode;
			ProductTitle = product?.Title ?? string.Empty;
		}

		/// <summary>상품 정의. 정의에 없는 상품의 주문(<see cref="IapResultCodes.UnknownProduct"/>)이면 null 이고, 이런 주문은 지급자에 오지 않습니다</summary>
		public IapProductDefinition Definition { get; }

		/// <summary>게임 상품 ID. 정의에 없는 상품이면 스토어가 준 ID(없으면 null)</summary>
		public string ProductId { get; }

		/// <summary>지금 플랫폼 스토어의 상품 ID. 정의에 없는 상품이면 null</summary>
		public string StoreProductId => Definition?.StoreSpecificId;

		public string StoreName { get; }

		/// <summary>스토어 거래 ID(Google 은 구매 토큰). 서버는 영수증과 함께 이것으로 중복 지급을 막습니다</summary>
		public string TransactionId { get; }

		/// <summary>스토어 영수증. 없으면 빈 문자열</summary>
		public string Receipt { get; }

		/// <summary>
		/// 거래 하나에 서명한 데이터(App Store StoreKit 2 의 JWS). App Store 는 <see cref="Receipt"/> 가 앱 영수증이라 비어 있을 수 있으니,
		/// 서버 검증에는 이 값을 쓰세요. 없으면 null
		/// </summary>
		public string SignedTransaction { get; }

		/// <summary>구독·복원의 원래 거래 ID(App Store). 없으면 null</summary>
		public string OriginalTransactionId { get; }

		public IapOrderOrigin Origin { get; }

		/// <summary>
		/// <see cref="IIapService.PurchaseAsync"/> 에 넘긴 게임 데이터(고른 보상 등). 메모리에만 있어 이전 실행의 결제를 복구할 때는 null 입니다.
		/// 지급에 꼭 필요한 선택은 결제 전에 서버에 저장하거나, 복구 때 다시 물으세요
		/// </summary>
		public string Payload { get; }

		/// <summary>조회한 현지 통화 가격. 상품 정보가 없으면 0</summary>
		public decimal Price { get; }

		/// <summary>ISO 4217 통화 코드. 모르면 null</summary>
		public string CurrencyCode { get; }

		public string ProductTitle { get; }

		public override string ToString()
		{
			return $"{ProductId} (transaction {TransactionId}, {Origin})";
		}
	}
}
