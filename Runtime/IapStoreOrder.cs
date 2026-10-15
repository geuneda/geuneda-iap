using System;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 스토어 어댑터가 서비스에 넘기는 주문입니다. 미확정 주문(결제됐지만 아직 확정하지 않음)이거나,
	/// <see cref="IIapStore.FetchOrdersAsync"/> 의 보유 주문(이미 확정한 비소모성·구독)입니다.
	/// </summary>
	public sealed class IapStoreOrder
	{
		/// <param name="productId">게임 상품 ID(<see cref="IapProductDefinition.Id"/>). 스토어가 모르는 상품이면 스토어 상품 ID 나 null</param>
		/// <param name="transactionId">스토어 거래 ID. 비었으면 <see cref="ArgumentException"/>(서비스가 이것으로 중복을 거름)</param>
		/// <param name="receipt">서버 검증에 보낼 영수증. 없으면 빈 문자열</param>
		/// <param name="isKnownProduct">스토어가 상품 종류를 아는지. false 면 서비스가 지급·확정하지 않습니다</param>
		/// <param name="native">어댑터가 확정에 쓰는 원본 주문 객체</param>
		/// <param name="signedTransaction">거래 하나에 서명한 데이터(App Store 의 JWS). 없으면 null</param>
		/// <param name="originalTransactionId">구독·복원의 원래 거래 ID(App Store). 없으면 null</param>
		public IapStoreOrder(string productId, string transactionId, string receipt, bool isKnownProduct, object native,
			string signedTransaction = null, string originalTransactionId = null)
		{
			if (string.IsNullOrEmpty(transactionId))
			{
				throw new ArgumentException("Transaction id is empty", nameof(transactionId));
			}

			ProductId = string.IsNullOrEmpty(productId) ? null : productId;
			TransactionId = transactionId;
			Receipt = receipt ?? string.Empty;
			IsKnownProduct = isKnownProduct;
			Native = native;
			SignedTransaction = string.IsNullOrEmpty(signedTransaction) ? null : signedTransaction;
			OriginalTransactionId = string.IsNullOrEmpty(originalTransactionId) ? null : originalTransactionId;
		}

		public string ProductId { get; }

		public string TransactionId { get; }

		public string Receipt { get; }

		/// <summary>
		/// 스토어가 이 주문의 상품 종류를 아는지입니다. 모르는 주문을 확정하면 스토어가 잘못 처리할 수 있어
		/// (Google 은 소비 대신 승인만 함) 서비스는 지급도 확정도 하지 않고 <see cref="IapResultCodes.UnknownProduct"/> 로 알립니다.
		/// </summary>
		public bool IsKnownProduct { get; }

		/// <summary>어댑터 전용 원본 주문입니다. 서비스는 읽지 않고 <see cref="IIapStore.ConfirmAsync"/> 에 그대로 돌려줍니다.</summary>
		public object Native { get; }

		public string SignedTransaction { get; }

		public string OriginalTransactionId { get; }
	}
}
