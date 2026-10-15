namespace Geuneda.Iap.Server
{
	/// <summary>
	/// App Store 거래 서명(JWSTransactionDecodedPayload) 중 쓰는 필드입니다. 시각은 Unix 밀리초, <c>price</c> 는 통화 단위의 1000분의 1(milliunits)입니다.
	/// 이름은 JSON camelCase 와 같습니다(IapServerJson 의 Web 기본값).
	/// </summary>
	internal sealed class AppleTransactionPayload
	{
		public string TransactionId { get; set; }
		public string OriginalTransactionId { get; set; }
		public string BundleId { get; set; }
		public string ProductId { get; set; }
		public long? PurchaseDate { get; set; }
		public int? Quantity { get; set; }
		public string Type { get; set; }
		public string Environment { get; set; }
		public string AppAccountToken { get; set; }
		public long? RevocationDate { get; set; }
		public int? RevocationReason { get; set; }
		public long? ExpiresDate { get; set; }
		public long? Price { get; set; }
		public string Currency { get; set; }
	}
}
