using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// Android Publisher API 의 OAuth access 토큰을 줍니다. 기본 구현은 <see cref="GooglePlayOptions"/> 의 서비스 계정(Google.Apis.Auth)이고,
	/// 다른 방식(비밀 관리자, 테스트 대역)은 <see cref="IapServerBuilder.AddGooglePlay"/> 전에 이 인터페이스를 등록합니다.
	/// </summary>
	public interface IGooglePlayAccessTokenSource
	{
		/// <summary>범위 <c>https://www.googleapis.com/auth/androidpublisher</c> 의 토큰. 만료 전까지 재사용해도 됩니다</summary>
		Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
	}
}
