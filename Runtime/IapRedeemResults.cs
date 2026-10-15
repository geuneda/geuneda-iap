using System;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 결제 서버(<c>Server~</c> 의 <c>Geuneda.Iap.Server</c>) 와이어 형식과 지급 결과 사이의 변환입니다. HTTP 지급자(<c>Geuneda.Iap.Http</c>)가 쓰고,
	/// 실시간 RPC 처럼 다른 경로로 서버를 부르는 지급자도 같은 규칙으로 쓸 수 있습니다.
	/// </summary>
	public static class IapRedeemResults
	{
		/// <summary>주문을 지급 요청으로 바꿉니다.</summary>
		public static IapRedeemRequest ToRedeemRequest(this IapOrder order)
		{
			if (order == null)
			{
				throw new ArgumentNullException(nameof(order));
			}

			return new IapRedeemRequest
			{
				Store = order.StoreName,
				ProductId = order.ProductId,
				TransactionId = order.TransactionId,
				Receipt = order.Receipt,
				SignedTransaction = order.SignedTransaction,
				Payload = order.Payload
			};
		}

		/// <summary>
		/// 서버 응답을 지급 결과로 바꿉니다. 결과의 <see cref="FulfillmentResult.Response"/> 는 응답 자체입니다(지급 결과 <see cref="IapRedeemResponse{TGrant}.Grant"/>,
		/// 실결제 금액). 응답이 없거나 상태를 모르면 거래를 닫지 않도록 <see cref="FulfillmentResult.RetryLater"/> 입니다.
		/// </summary>
		public static FulfillmentResult ToFulfillmentResult<TGrant>(this IapRedeemResponse<TGrant> response)
		{
			if (response == null)
			{
				return FulfillmentResult.RetryLater("Empty redeem response");
			}

			var message = response.Message == null ? $"code {response.Code}" : $"code {response.Code}: {response.Message}";

			switch (response.Status)
			{
				case IapRedeemStatus.Granted:
					return FulfillmentResult.Granted(response);
				case IapRedeemStatus.AlreadyGranted:
					return FulfillmentResult.AlreadyGranted(response, message);
				case IapRedeemStatus.RetryLater:
					return FulfillmentResult.RetryLater(message);
				case IapRedeemStatus.Rejected:
					return FulfillmentResult.Rejected(message);
				default:
					return FulfillmentResult.RetryLater($"Unknown redeem status {(int) response.Status}, {message}");
			}
		}
	}
}
