using System;
using System.Collections.Generic;
using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	internal enum OrderState
	{
		/// <summary>막 만들어져 아직 큐에 넣지 않음(기본값)</summary>
		New,

		/// <summary>처리 큐에 있음</summary>
		Queued,

		/// <summary>지급자나 확정을 기다리는 중</summary>
		Working,

		/// <summary>백오프가 끝나기를 기다림(지급 다시 시도 또는 확정만 다시 시도)</summary>
		Waiting,

		/// <summary>지급자가 거절함. 명시적 복구·같은 상품 구매 때만 다시 시도</summary>
		Rejected,

		/// <summary>스토어나 정의가 상품을 몰라 처리하지 않음</summary>
		Unresolved,

		/// <summary>결제창이 열린 상품의 새 주문을 조회가 먼저 찾음. 결제창의 답이 올 때까지 지급하지 않음(그 구매의 것일 수 있음)</summary>
		Held,
	}

	/// <summary>서비스가 아직 닫지 못한 스토어 거래 하나입니다. 거래 ID 로 하나만 있습니다.</summary>
	internal sealed class OrderEntry
	{
		public OrderEntry(IapStoreOrder storeOrder, IapOrder order)
		{
			StoreOrder = storeOrder;
			Order = order;
		}

		/// <summary>스토어가 마지막으로 준 주문. 확정은 가장 최근 원본으로 합니다</summary>
		public IapStoreOrder StoreOrder { get; set; }

		public IapOrder Order { get; set; }

		/// <summary>들어온 경로. <see cref="Order"/> 를 다시 만들 때 씁니다</summary>
		public IapOrderOrigin Origin { get; set; }

		public string Payload { get; set; }

		public string TransactionId => StoreOrder.TransactionId;

		public OrderState State { get; set; }

		/// <summary>지급자가 지급(또는 이미 지급)을 확인함. 이후에는 지급자를 다시 부르지 않고 확정만 합니다</summary>
		public bool Granted { get; set; }

		public FulfillmentResult Fulfillment { get; set; }

		/// <summary><see cref="IIapService.OrderSettled"/> 를 올렸는지</summary>
		public bool Announced { get; set; }

		/// <summary><see cref="IapResultCodes.UnknownProduct"/> 를 알렸는지</summary>
		public bool UnresolvedReported { get; set; }

		/// <summary>연속 실패 수(백오프 단계)</summary>
		public int Failures { get; set; }

		/// <summary>다시 시도할 단조 시각</summary>
		public TimeSpan RetryAt { get; set; }

		/// <summary>이 주문의 결과를 받을 <see cref="IIapService.PurchaseAsync"/> 호출</summary>
		public PurchaseRequest Purchase { get; set; }

		/// <summary>다음 처리 결과를 기다리는 호출자</summary>
		public List<Waiter<Result<PurchaseSettlement>>> Waiters { get; } = new List<Waiter<Result<PurchaseSettlement>>>();
	}
}
