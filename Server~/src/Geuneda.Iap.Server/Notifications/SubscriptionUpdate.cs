using System;

namespace Geuneda.Iap.Server
{
	/// <summary>알림 처리기(<see cref="IPurchaseNotificationHandler"/>)에 넘기는 구독 상태 스냅숏입니다.</summary>
	public sealed record SubscriptionUpdate
	{
		/// <summary>원장 스토어(<see cref="IapStoreNames.GooglePlay"/>, <see cref="IapStoreNames.AppleAppStore"/>)</summary>
		public required string Store { get; init; }

		/// <summary>구독 ID. Google 은 구매 토큰, App Store 는 원래 거래 ID(<c>originalTransactionId</c>)</summary>
		public required string SubscriptionId { get; init; }

		/// <summary>이 구독이 대신한 이전 구독(Google 업그레이드·재가입의 <c>linkedPurchaseToken</c>). 없으면 null</summary>
		public string LinkedSubscriptionId { get; init; }

		/// <summary>지금 구독 중인 스토어 상품 ID</summary>
		public string StoreProductId { get; init; }

		/// <summary>서버 상품. 상품 목록에 없는 스토어 상품이면 null</summary>
		public IapServerProduct Product { get; init; }

		/// <summary>
		/// 이 구독을 지급받은 계정(원장 기록). 클라이언트가 아직 지급 요청을 하지 않았으면 null 이고 이 갱신은 다시 오지 않습니다. <see cref="SubscriptionId"/> 로 저장해 두고,
		/// 지급자가 <see cref="VerifiedPurchase.SubscriptionId"/> 로 계정에 연결하세요(관측 시각이 더 늦은 쪽, 같으면 이 알림 상태를 우선 — App Store 거래 서명에는 자동 갱신·유예 정보가 없음)
		/// </summary>
		public string AccountId { get; init; }

		public IapSubscriptionState State { get; init; }

		/// <summary>현재 결제 기간의 만료 시각. 모르면 null</summary>
		public DateTimeOffset? ExpiresAt { get; init; }

		/// <summary>결제 유예(<see cref="IapSubscriptionState.GracePeriod"/>)가 끝나는 시각(App Store <c>gracePeriodExpiresDate</c>). 권한은 이때까지 유지합니다. 모르면 null</summary>
		public DateTimeOffset? GracePeriodExpiresAt { get; init; }

		/// <summary>자동 갱신이 켜져 있는지. 모르면 null</summary>
		public bool? AutoRenewing { get; init; }

		/// <summary>테스트 결제(Google 라이선스 테스터, App Store 샌드박스)인지</summary>
		public bool IsTest { get; init; }

		/// <summary>스토어 알림 종류(Google <c>SUBSCRIPTION_RENEWED</c> 등, App Store <c>DID_RENEW</c> 등)</summary>
		public string NotificationType { get; init; }

		/// <summary>
		/// 이 상태의 관측 시각. App Store 는 알림·거래 서명의 <c>signedDate</c>, Google 은 서버가 원장의 구독 잠금 안에서 조회한 시각(저장된 것보다 늦게 맞춤)입니다.
		/// 원장이 이보다 오래된 상태는 넘기지 않습니다
		/// </summary>
		public DateTimeOffset ObservedAt { get; init; }

		/// <summary>마지막 거래 ID(App Store) 또는 마지막 결제 주문 ID(Google). 없으면 null</summary>
		public string LatestTransactionId { get; init; }

		public override string ToString()
		{
			return $"{Store} subscription {SubscriptionId} {StoreProductId} {State} until {ExpiresAt:O} ({NotificationType})";
		}
	}
}
