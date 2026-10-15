using System;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 영수증 원장의 기록 하나입니다. (<see cref="Store"/>, <see cref="TransactionId"/>) 가 유일 키이고, 지급과 같은 트랜잭션에서 씁니다.
	/// 원장 구현(<see cref="IPurchaseLedger"/>)이 DB 에서 읽어 만들 수 있게 모든 값을 init 으로 둡니다.
	/// </summary>
	public sealed record PurchaseRecord
	{
		/// <summary>검증기의 <see cref="IStoreVerifier.LedgerStore"/>(Mac App Store 거래도 <see cref="IapStoreNames.AppleAppStore"/>)</summary>
		public required string Store { get; init; }

		/// <summary>스토어 거래 ID(Google 은 구매 토큰)</summary>
		public required string TransactionId { get; init; }

		/// <summary>지급받은 계정</summary>
		public required string AccountId { get; init; }

		/// <summary>게임 상품 ID</summary>
		public required string ProductId { get; init; }

		public string StoreProductId { get; init; }

		public string OrderId { get; init; }

		public string OriginalTransactionId { get; init; }

		public int Quantity { get; init; } = 1;

		public bool IsTest { get; init; }

		public DateTimeOffset? PurchasedAt { get; init; }

		/// <summary>지급해 기록한 시각</summary>
		public DateTimeOffset RedeemedAt { get; init; }

		public decimal? PaidAmount { get; init; }

		public string PaidCurrency { get; init; }

		public DateTimeOffset? ExpiresAt { get; init; }

		/// <summary>지급자 응답(<see cref="GrantResult.Response"/>)의 JSON. 응답이 없었으면 null</summary>
		public string GrantResponse { get; init; }

		/// <summary>
		/// 지급 뒤 서버가 스토어에 확인(Google acknowledge)해야 하는데 아직 못 했으면 다음 시도 시각, 없으면 null 입니다. 지급과 같은 트랜잭션에서 지급 시각으로 기록하고,
		/// 확인하면 <see cref="IPurchaseLedger.MarkAcknowledgedAsync"/> 가 null 로, 실패하면 확인 작업자가 <see cref="IPurchaseLedger.DeferAcknowledgementAsync"/> 로 뒤로 미룹니다.
		/// 서버가 그 사이 죽어도 확인 작업자가 다시 시도합니다(DB 에서는 이 열에 색인)
		/// </summary>
		public DateTimeOffset? AcknowledgementDueAt { get; init; }
	}
}
