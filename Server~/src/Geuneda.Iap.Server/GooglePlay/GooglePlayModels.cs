using System.Collections.Generic;

namespace Geuneda.Iap.Server
{
	// Android Publisher API v3 응답 중 쓰는 필드만 옮긴 모델입니다. 열거형은 문자열 그대로 두고(새 값이 와도 해석이 실패하지 않게), 시각은 RFC 3339 문자열입니다.
	// 이름은 JSON camelCase 와 같습니다(IapServerJson 의 Web 기본값).

	/// <summary><c>purchases.productsv2.getproductpurchasev2</c> 응답(ProductPurchaseV2)</summary>
	internal sealed class GoogleProductPurchase
	{
		public List<GoogleProductLineItem> ProductLineItem { get; set; }
		public GooglePurchaseStateContext PurchaseStateContext { get; set; }
		public GoogleTestPurchaseContext TestPurchaseContext { get; set; }
		public string OrderId { get; set; }
		public string ObfuscatedExternalAccountId { get; set; }
		public string PurchaseCompletionTime { get; set; }
		public string AcknowledgementState { get; set; }
	}

	internal sealed class GoogleProductLineItem
	{
		public string ProductId { get; set; }
		public GoogleProductOfferDetails ProductOfferDetails { get; set; }
	}

	internal sealed class GoogleProductOfferDetails
	{
		public int? Quantity { get; set; }

		/// <summary>아직 환불할 수 있는 수량(일부 환불 뒤 줄어듦)</summary>
		public int? RefundableQuantity { get; set; }
	}

	internal sealed class GooglePurchaseStateContext
	{
		public string PurchaseState { get; set; }
	}

	internal sealed class GoogleTestPurchaseContext
	{
		public string FopType { get; set; }
	}

	/// <summary><c>purchases.subscriptionsv2.get</c> 응답(SubscriptionPurchaseV2)</summary>
	internal sealed class GoogleSubscriptionPurchase
	{
		public List<GoogleSubscriptionLineItem> LineItems { get; set; }
		public string StartTime { get; set; }
		public string SubscriptionState { get; set; }
		public string LinkedPurchaseToken { get; set; }

		/// <summary>있으면 테스트 결제(필드 없는 객체)</summary>
		public object TestPurchase { get; set; }

		public string AcknowledgementState { get; set; }
		public GoogleExternalAccountIdentifiers ExternalAccountIdentifiers { get; set; }
	}

	internal sealed class GoogleSubscriptionLineItem
	{
		public string ProductId { get; set; }
		public string ExpiryTime { get; set; }
		public string LatestSuccessfulOrderId { get; set; }
		public GoogleAutoRenewingPlan AutoRenewingPlan { get; set; }
	}

	internal sealed class GoogleAutoRenewingPlan
	{
		public bool? AutoRenewEnabled { get; set; }
	}

	internal sealed class GoogleExternalAccountIdentifiers
	{
		public string ObfuscatedExternalAccountId { get; set; }
	}

	/// <summary><c>orders.get</c> 응답(Order) 중 실결제 금액</summary>
	internal sealed class GoogleOrder
	{
		public GoogleMoney Total { get; set; }
	}

	/// <summary>google.type.Money. <c>units</c> 는 int64 라 JSON 에서 문자열입니다</summary>
	internal sealed class GoogleMoney
	{
		public string CurrencyCode { get; set; }
		public string Units { get; set; }
		public int? Nanos { get; set; }
	}
}
