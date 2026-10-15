using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	internal enum PubSubTokenStatus
	{
		Valid = 1,
		Invalid = 2,

		/// <summary>서명 키를 받지 못함. Pub/Sub 이 다시 보내게 합니다</summary>
		Unavailable = 3,
	}

	/// <summary>
	/// Pub/Sub 푸시 요청의 OIDC 토큰을 검증합니다. Google 이 서명했는지, 발급자, 대상(<see cref="GooglePlayOptions.NotificationAudience"/>), 만료,
	/// 서비스 계정 이메일(<see cref="GooglePlayOptions.NotificationServiceAccountEmail"/>)과 이메일 확인 여부를 봅니다.
	/// </summary>
	internal sealed class GooglePubSubTokenVerifier
	{
		private readonly GooglePlayOptions _options;

		public GooglePubSubTokenVerifier(IOptions<GooglePlayOptions> options)
		{
			_options = options.Value;
		}

		public async Task<(PubSubTokenStatus Status, string Message)> VerifyAsync(string token, CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(token))
			{
				return (PubSubTokenStatus.Invalid, "Missing bearer token");
			}

			// 토큰 시각(발급·만료)은 Google.Apis.Auth 가 시스템 시계로 확인한다(시계를 바꿀 수 있는 공개 설정이 없음)
			var settings = new SignedTokenVerificationOptions { CertificatesUrl = _options.NotificationCertificatesUrl?.AbsoluteUri };
			settings.TrustedAudiences.Add(_options.NotificationAudience);
			settings.TrustedIssuers.Add("https://accounts.google.com");
			settings.TrustedIssuers.Add("accounts.google.com");
			GoogleJsonWebSignature.Payload payload;

			try
			{
				payload = await JsonWebSignature.VerifySignedTokenAsync<GoogleJsonWebSignature.Payload>(token, settings, cancellationToken);
			}
			catch (InvalidJwtException e)
			{
				return (PubSubTokenStatus.Invalid, e.Message);
			}
			catch (HttpRequestException e)
			{
				return (PubSubTokenStatus.Unavailable, $"Could not fetch Google signing keys: {e.Message}");
			}
			catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				return (PubSubTokenStatus.Unavailable, "Fetching Google signing keys timed out");
			}

			if (payload.Email != _options.NotificationServiceAccountEmail || payload.EmailVerified != true)
			{
				return (PubSubTokenStatus.Invalid, $"Token is for {payload.Email}, not the push service account");
			}

			return (PubSubTokenStatus.Valid, null);
		}
	}
}
