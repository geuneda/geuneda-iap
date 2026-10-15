using System;

namespace Geuneda.Iap.Server
{
	/// <summary>알림 처리기(<see cref="IPurchaseNotificationHandler"/>)에 넘기는 환불 변경 하나입니다.</summary>
	public sealed class PurchaseRefund
	{
		internal PurchaseRefund(PurchaseRecord record, IapServerProduct product, int quantity, int refundedQuantity, string reason, DateTimeOffset observedAt)
		{
			Record = record;
			Product = product;
			Quantity = quantity;
			RefundedQuantity = refundedQuantity;
			Reason = reason;
			ObservedAt = observedAt;
		}

		/// <summary>바뀌기 전의 원장 기록(지급한 계정, 상품, 수량, 지급 응답)</summary>
		public PurchaseRecord Record { get; }

		public string AccountId => Record.AccountId;

		/// <summary>서버 상품. 상품 목록에서 빠진 상품이면 null 입니다(판매를 그만둔 상품도 빼지 마세요)</summary>
		public IapServerProduct Product { get; }

		/// <summary>이번에 회수(또는 다시 지급)할 수량. 1 이상</summary>
		public int Quantity { get; }

		/// <summary>이 변경 뒤의 누적 환불 수량</summary>
		public int RefundedQuantity { get; }

		/// <summary>모든 수량이 환불됐는지</summary>
		public bool IsFullRefund => RefundedQuantity >= Math.Max(1, Record.Quantity);

		/// <summary>로그용 스토어 알림 종류(예: <c>REFUND</c>, <c>voidedPurchase:full</c>)</summary>
		public string Reason { get; }

		/// <summary>스토어 이벤트 시각</summary>
		public DateTimeOffset ObservedAt { get; }

		public override string ToString()
		{
			return $"{Record.Store} {Record.ProductId} (transaction {Record.TransactionId}, {Quantity} of {Record.Quantity}, {Reason})";
		}
	}
}
