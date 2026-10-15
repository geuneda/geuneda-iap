// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 지급 요청의 스토어 이름입니다(<see cref="IapRedeemRequest.Store"/>). Unity IAP 의 스토어 이름과 같아서 <c>IapOrder.StoreName</c> 을 그대로 보냅니다.
	/// 서버(<c>Server~</c>)와 함께 컴파일합니다.
	/// </summary>
	public static class IapStoreNames
	{
		public const string GooglePlay = "GooglePlay";

		public const string AppleAppStore = "AppleAppStore";

		public const string MacAppStore = "MacAppStore";

		/// <summary>Unity IAP 에디터 가짜 스토어. 개발 서버에서만 받습니다</summary>
		public const string Fake = "fake";
	}
}
