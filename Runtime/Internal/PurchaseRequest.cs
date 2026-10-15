using System.Collections.Generic;
using System.Threading;
using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>진행 중인 <see cref="IIapService.PurchaseAsync"/> 호출 하나입니다. 한 번에 하나만 있습니다.</summary>
	internal sealed class PurchaseRequest
	{
		public PurchaseRequest(IapProductDefinition definition, string payload, CancellationToken cancellationToken)
		{
			Definition = definition;
			Payload = payload;
			Waiter = new Waiter<Result<PurchaseSettlement>>(cancellationToken, Result<PurchaseSettlement>.Fail(ResultCodes.Cancelled, "Purchase wait cancelled"));
		}

		public IapProductDefinition Definition { get; }

		public string Payload { get; }

		public Waiter<Result<PurchaseSettlement>> Waiter { get; }

		/// <summary>결제창을 열었고 스토어의 답을 기다리는 중</summary>
		public bool AwaitingStore { get; set; }

		/// <summary>이 구매의 결과를 낼 주문</summary>
		public OrderEntry Entry { get; set; }

		/// <summary>결제창의 답을 기다리는 동안 조회가 찾은 같은 상품의 새 주문(<see cref="OrderState.Held"/>)</summary>
		public List<OrderEntry> Held { get; } = new List<OrderEntry>();

		/// <summary>스토어 응답 제한 시간 타이머</summary>
		public CancellationTokenSource StoreTimer { get; set; }
	}
}
