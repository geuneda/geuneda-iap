using System;

namespace Geuneda.Iap.Server
{
	/// <summary><see cref="IPurchaseLedger.UpdateRefundAsync"/> 에 넘기는 환불 변경 하나입니다.</summary>
	public sealed class RefundChange
	{
		/// <summary>원장 스토어(<see cref="IStoreVerifier.LedgerStore"/>)</summary>
		public required string Store { get; init; }

		public required string TransactionId { get; init; }

		/// <summary>스토어가 알려 준 누적 환불 수량</summary>
		public required int RefundedQuantity { get; init; }

		/// <summary>이 변경의 시각(서명된 이벤트 시각, 또는 조회 시각)</summary>
		public required DateTimeOffset ChangedAt { get; init; }

		public RefundUpdateMode Mode { get; init; } = RefundUpdateMode.EventOrdered;

		/// <summary>원장에 없을 때 남기는 지급 전 환불 기록의 게임 상품 ID(모르면 스토어 상품 ID)</summary>
		public required string ProductId { get; init; }

		public string StoreProductId { get; init; }

		/// <summary>원장에 없을 때 남기는 지급 전 환불 기록의 구매 수량(스토어가 알려 준 값). 1 이상</summary>
		public int Quantity { get; init; } = 1;

		public override string ToString()
		{
			return $"{Store} {ProductId} (transaction {TransactionId}, refunded {RefundedQuantity}, {Mode})";
		}
	}
}
