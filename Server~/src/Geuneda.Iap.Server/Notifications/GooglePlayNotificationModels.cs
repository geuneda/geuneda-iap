using System.Text.Json.Serialization;

namespace Geuneda.Iap.Server
{
	// Pub/Sub 푸시 본문과 Google Play 실시간 개발자 알림(DeveloperNotification) 중 쓰는 필드입니다. 이름은 JSON camelCase 와 같습니다.

	/// <summary>Pub/Sub 푸시 요청 본문</summary>
	internal sealed class PubSubPushEnvelope
	{
		public PubSubMessage Message { get; set; }
		public string Subscription { get; set; }
	}

	internal sealed class PubSubMessage
	{
		/// <summary>base64 로 인코딩한 DeveloperNotification JSON</summary>
		public string Data { get; set; }

		public string MessageId { get; set; }
	}

	/// <summary>DeveloperNotification. 알림 종류 필드는 하나만 옵니다</summary>
	internal sealed class GoogleDeveloperNotification
	{
		public string Version { get; set; }
		public string PackageName { get; set; }

		/// <summary>int64 라 문자열로 올 수 있습니다</summary>
		[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
		public long? EventTimeMillis { get; set; }

		public GoogleOneTimeProductNotification OneTimeProductNotification { get; set; }
		public GoogleSubscriptionNotification SubscriptionNotification { get; set; }
		public GoogleVoidedPurchaseNotification VoidedPurchaseNotification { get; set; }
		public object TestNotification { get; set; }
	}

	internal sealed class GoogleOneTimeProductNotification
	{
		public int NotificationType { get; set; }
		public string PurchaseToken { get; set; }
		public string Sku { get; set; }
	}

	internal sealed class GoogleSubscriptionNotification
	{
		public int NotificationType { get; set; }
		public string PurchaseToken { get; set; }
	}

	internal sealed class GoogleVoidedPurchaseNotification
	{
		public const int SubscriptionProduct = 1;
		public const int PartialRefund = 2;

		public string PurchaseToken { get; set; }
		public string OrderId { get; set; }

		/// <summary>1 구독, 2 일회성</summary>
		public int ProductType { get; set; }

		/// <summary>1 전체 환불, 2 수량 일부 환불(여러 개 구매만, 여러 번 올 수 있음)</summary>
		public int RefundType { get; set; }
	}
}
