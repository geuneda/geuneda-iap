using System;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 서버가 합친 구독 권한 스냅숏입니다. 서버 알림과 구독 지급을 원장의 구독 잠금 안에서 합쳐 바뀔 때마다 알림 처리기(<see cref="IPurchaseNotificationHandler"/>)에 넘기고,
	/// 원장(<see cref="IPurchaseLedger.UpdateSubscriptionAsync"/>)이 마지막 것을 저장합니다. 게임은 받은 것을 <see cref="SubscriptionId"/> 로 그대로 저장하면 됩니다.
	/// </summary>
	/// <remarks>
	/// 합치는 규칙: Google 은 구독 잠금 안에서 지금 상태를 다시 읽은 것이 항상 이깁니다. App Store(가짜·사용자 정의 스토어도 지급한 거래로)는 거래·알림이 그 결제 기간의 것이라 기간(<see cref="PeriodStartedAt"/>)이 뒤인 것이 이기고,
	/// 같은 기간이면 늦게 서명된 알림이 이깁니다. 지급한 거래 서명에는 자동 갱신·유예 정보가 없어 뒤 기간일 때만 상태를 바꾸고, 그 밖에는 계정만 연결합니다.
	/// </remarks>
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
		/// 권한을 받은 계정. 스냅숏이 정합니다(소유자를 정한 지급보다 같거나 뒤 기간의 거래를 지급받으면 그 계정, <see cref="AccountPeriodStartedAt"/>). 알림은 비었을 때만 원장에서 찾습니다. 클라이언트가 아직 지급 요청을
		/// 하지 않았으면 null 이고, 그 지급이 오면 서버가 같은 트랜잭션에서 계정을 넣은 스냅숏을 다시 넘깁니다. 바뀌면(다른 계정이 뒤 기간 거래를 지급받음) 권한을 그 계정으로 옮기세요
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

		/// <summary>이 스냅숏을 만든 일(Google <c>SUBSCRIPTION_RENEWED</c> 등, App Store <c>DID_RENEW</c> 등, 구독 지급이면 <c>REDEEM</c>)</summary>
		public string NotificationType { get; init; }

		/// <summary>
		/// 이 상태를 정한 정보의 시각. App Store 는 알림의 <c>signedDate</c>(지급한 거래 서명만으로 정한 상태면 그 거래의 구매 시각), Google 은 서버가 구독 잠금 안에서 조회한 시각
		/// (저장된 것보다 늦게 맞춤)입니다. 같은 기간 안에서 이보다 이른 알림은 합치지 않습니다
		/// </summary>
		public DateTimeOffset ObservedAt { get; init; }

		/// <summary>결제 기간 기준: 이 상태가 말하는 거래의 구매 시각(App Store <c>purchaseDate</c>, 가짜·사용자 정의 스토어는 검증한 구매 시각). 갱신·업그레이드마다 늦어집니다. Google 은 null</summary>
		public DateTimeOffset? PeriodStartedAt { get; init; }

		/// <summary>
		/// <see cref="AccountId"/> 를 정한 지급 거래의 기간(구매 시각). 소유권 순서에 씁니다: 그 이후 기간(또는 같은 기간)의 거래를 지급받은 계정이 소유자가 되고, 앞 기간 거래를 늦게 지급하면 바뀌지 않습니다.
		/// 알림은 상태 기간(<see cref="PeriodStartedAt"/>)만 올리고 이 값은 바꾸지 않습니다. Google 은 null(구매 토큰 하나가 한 번만 지급됨)
		/// </summary>
		public DateTimeOffset? AccountPeriodStartedAt { get; init; }

		/// <summary>마지막 거래 ID(App Store) 또는 마지막 결제 주문 ID(Google). 없으면 null</summary>
		public string LatestTransactionId { get; init; }

		public override string ToString()
		{
			return $"{Store} subscription {SubscriptionId} {StoreProductId} {State} until {ExpiresAt:O} ({NotificationType})";
		}
	}
}
