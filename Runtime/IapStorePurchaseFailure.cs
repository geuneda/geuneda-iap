// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary><see cref="IIapStore.PurchaseFailed"/> 의 내용입니다.</summary>
	public readonly struct IapStorePurchaseFailure
	{
		/// <param name="productId">실패한 상품의 게임 상품 ID. 스토어가 상품을 알아내지 못했으면 null</param>
		/// <param name="reason">실패 이유</param>
		/// <param name="message">로그용 설명</param>
		public IapStorePurchaseFailure(string productId, IapStoreFailureReason reason, string message)
		{
			ProductId = string.IsNullOrEmpty(productId) ? null : productId;
			Reason = reason;
			Message = message;
		}

		public string ProductId { get; }

		public IapStoreFailureReason Reason { get; }

		public string Message { get; }
	}
}
