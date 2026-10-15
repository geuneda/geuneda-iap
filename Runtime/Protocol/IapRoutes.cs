// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 결제 서버 경로입니다. 서버 접두사(예: <c>/iap</c>) 아래의 상대 경로이고, 클라이언트는 접두사까지 담은 기본 주소(예: <c>https://example.com/iap/</c>)에 붙입니다.
	/// </summary>
	public static class IapRoutes
	{
		/// <summary>POST <see cref="IapRedeemRequest"/> → <see cref="IapRedeemResponse{TGrant}"/>. 계정 인증 필요</summary>
		public const string Purchases = "purchases";
	}
}
