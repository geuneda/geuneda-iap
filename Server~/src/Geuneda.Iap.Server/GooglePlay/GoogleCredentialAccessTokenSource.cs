using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 서비스 계정 키(JSON 내용·파일)나 Application Default Credentials 로 토큰을 받습니다. 키는 만들 때 읽어 잘못된 키가 시작할 때 드러납니다.
	/// 토큰 캐시와 만료 전 갱신은 Google.Apis.Auth 가 합니다.
	/// </summary>
	internal sealed class GoogleCredentialAccessTokenSource : IGooglePlayAccessTokenSource
	{
		private const string Scope = "https://www.googleapis.com/auth/androidpublisher";

		private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
		private ITokenAccess _credential;

		public GoogleCredentialAccessTokenSource(IOptions<GooglePlayOptions> options)
		{
			var settings = options.Value;

			if (settings.ServiceAccountKeyJson != null)
			{
				_credential = CredentialFactory.FromJson<ServiceAccountCredential>(settings.ServiceAccountKeyJson).ToGoogleCredential().CreateScoped(Scope);
			}
			else if (settings.ServiceAccountKeyFile != null)
			{
				_credential = CredentialFactory.FromFile<ServiceAccountCredential>(settings.ServiceAccountKeyFile).ToGoogleCredential().CreateScoped(Scope);
			}
		}

		public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
		{
			var credential = Volatile.Read(ref _credential) ?? await LoadDefaultAsync(cancellationToken);
			return await credential.GetAccessTokenForRequestAsync(null, cancellationToken);
		}

		private async Task<ITokenAccess> LoadDefaultAsync(CancellationToken cancellationToken)
		{
			await _gate.WaitAsync(cancellationToken);

			try
			{
				if (_credential == null)
				{
					var credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken);
					Volatile.Write(ref _credential, credential.CreateScoped(Scope));
				}

				return _credential;
			}
			finally
			{
				_gate.Release();
			}
		}
	}
}
