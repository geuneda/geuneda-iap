using System;

namespace Geuneda.Iap.Server
{
	/// <summary>구독의 지금 상태입니다. 스토어마다 다른 상태를 이 값으로 맞춥니다.</summary>
	public enum IapSubscriptionState
	{
		/// <summary>이용 중(자동 갱신 켜짐)</summary>
		Active = 1,

		/// <summary>이용 중이지만 자동 갱신을 껐음. 만료 시각까지 권한이 있습니다</summary>
		Canceled = 2,

		/// <summary>갱신 결제가 실패해 유예 기간 중. 권한을 유지합니다</summary>
		GracePeriod = 3,

		/// <summary>유예 뒤에도 결제가 안 돼 보류됨(Google 계정 보류, App Store 결제 재시도). 권한이 없습니다</summary>
		OnHold = 4,

		/// <summary>사용자가 일시 중지함(Google). 권한이 없습니다</summary>
		Paused = 5,

		/// <summary>만료됨</summary>
		Expired = 6,
	}

	/// <summary>스토어가 확인한 결제입니다. 검증기(<see cref="IStoreVerifier"/>)가 만듭니다.</summary>
	public sealed class VerifiedPurchase
	{
		/// <summary>검증기의 <see cref="IStoreVerifier.LedgerStore"/>(Mac App Store 거래도 <see cref="IapStoreNames.AppleAppStore"/>)</summary>
		public required string Store { get; init; }

		/// <summary>스토어 거래 ID(Google 은 구매 토큰). 원장의 유일 키입니다</summary>
		public required string TransactionId { get; init; }

		/// <summary>거래의 스토어 상품 ID</summary>
		public required string StoreProductId { get; init; }

		public IapProductType ProductType { get; init; }

		/// <summary>스토어 주문 ID(Google <c>GPA.</c>). 없으면 null</summary>
		public string OrderId { get; init; }

		/// <summary>구독·복원의 원래 거래 ID(App Store). 없으면 null</summary>
		public string OriginalTransactionId { get; init; }

		/// <summary>구매 수량. 모르면 1</summary>
		public int Quantity { get; init; } = 1;

		/// <summary>결제가 끝난 시각. 모르면 null. 구독은 이 거래가 낸 결제 기간의 시작이라, Google 이 아닌 스토어에서 서버 알림을 쓰면 필수입니다(구독 기간 순서의 기준)</summary>
		public DateTimeOffset? PurchasedAt { get; init; }

		/// <summary>구독 만료 시각. 구독이 아니면 null</summary>
		public DateTimeOffset? ExpiresAt { get; init; }

		/// <summary>
		/// 검증 때 읽은 구독 상태. 구독이 아니면 null 입니다. 서버 알림을 쓰면 서버가 지급 트랜잭션 안에서 이 구독을 합친 스냅숏(<see cref="SubscriptionUpdate"/>)을 알림 처리기에 넘기므로
		/// 지급자가 이 값으로 구독 권한을 쓰지 마세요(알림 잠금 밖에서 읽은 것이라 더 새 상태보다 늦을 수 있고, App Store 는 만료 시각으로만 정한 것). 서버 알림을 쓰지 않을 때만 지급자가 이 값을 씁니다.
		/// 사용자 정의 검증기는 구독 거래에 이 값을 채워야 합니다(서버 알림을 쓰면 이 값으로 스냅숏을 만들고, 없으면 지급이 예외로 실패)
		/// </summary>
		public IapSubscriptionState? SubscriptionState { get; init; }

		/// <summary>
		/// 서버 알림(<see cref="SubscriptionUpdate.SubscriptionId"/>)과 같은 구독 ID 입니다(Google 은 구매 토큰, App Store 는 원래 거래 ID).
		/// 구독이 아니면 null
		/// </summary>
		public string SubscriptionId => ProductType == IapProductType.Subscription ? OriginalTransactionId ?? TransactionId : null;

		/// <summary>테스트 결제(Google 라이선스 테스터, App Store 샌드박스, 가짜 스토어)인지</summary>
		public bool IsTest { get; init; }

		/// <summary>구독의 자동 갱신이 켜져 있는지(Google). 모르면 null(App Store 거래 서명에는 없음). <see cref="SubscriptionState"/> 와 같은 때만 씁니다</summary>
		public bool? AutoRenewing { get; init; }

		/// <summary>
		/// 구매 때 앱이 넣은 계정 표시(Google <c>obfuscatedExternalAccountId</c>, App Store <c>appAccountToken</c>). 넣지 않았으면 null 입니다.
		/// 넣었다면 지급자가 요청 계정과 맞는지 확인할 수 있습니다
		/// </summary>
		public string AccountToken { get; init; }

		/// <summary>스토어가 확인한 실결제 금액. 모르면 null</summary>
		public decimal? PaidAmount { get; init; }

		/// <summary><see cref="PaidAmount"/> 의 ISO 4217 통화 코드. 모르면 null</summary>
		public string PaidCurrency { get; init; }

		/// <summary>지급 뒤 서버가 스토어에 확인(acknowledge)해야 하는지(Google 미확인 결제이고 <see cref="GooglePlayOptions.AcknowledgeAfterGrant"/> 가 켜짐). 원장의 <see cref="PurchaseRecord.AcknowledgementDueAt"/> 으로 기록합니다</summary>
		public bool RequiresAcknowledgement { get; init; }

		public override string ToString()
		{
			return $"{Store} {StoreProductId} (transaction {TransactionId})";
		}
	}
}
