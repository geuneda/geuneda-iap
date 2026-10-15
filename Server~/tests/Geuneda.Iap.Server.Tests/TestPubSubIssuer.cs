using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>
	/// Pub/Sub 푸시 OIDC 토큰을 흉내 냅니다. 테스트 RSA 키의 JWKS 를 127.0.0.1 빈 포트에서 내주고(<see cref="CertificatesUrl"/>), 그 키로 Google 형식의 토큰을 서명합니다.
	/// 토큰 시각은 실제 현재 시각입니다(Google.Apis.Auth 가 시스템 시계로 확인).
	/// </summary>
	internal sealed class TestPubSubIssuer : IAsyncDisposable
	{
		public const string Audience = "https://game.example.com/iap/notifications/google-play";
		public const string ServiceAccount = "rtdn-push@test-project.iam.gserviceaccount.com";
		private const string KeyId = "test-key";

		private readonly WebApplication _app;
		private readonly RSA _key;

		private TestPubSubIssuer(WebApplication app, RSA key, Uri certificatesUrl)
		{
			_app = app;
			_key = key;
			CertificatesUrl = certificatesUrl;
		}

		public Uri CertificatesUrl { get; }

		public static async Task<TestPubSubIssuer> StartAsync()
		{
			var key = RSA.Create(2048);
			var parameters = key.ExportParameters(false);
			var jwks = JsonSerializer.Serialize(new
			{
				keys = new[]
				{
					new
					{
						kty = "RSA", alg = "RS256", use = "sig", kid = KeyId, n = Base64Url.EncodeToString(parameters.Modulus),
						e = Base64Url.EncodeToString(parameters.Exponent)
					}
				}
			});
			var builder = WebApplication.CreateSlimBuilder();
			builder.WebHost.UseUrls("http://127.0.0.1:0");
			builder.Logging.ClearProviders();
			var app = builder.Build();
			app.MapGet("/certs", () => Results.Text(jwks, "application/json"));
			await app.StartAsync();
			var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
			return new TestPubSubIssuer(app, key, new Uri(address + "/certs"));
		}

		public string Token(string audience = Audience, string email = ServiceAccount, bool emailVerified = true, string issuer = "https://accounts.google.com",
			TimeSpan? age = null)
		{
			var issuedAt = DateTimeOffset.UtcNow - (age ?? TimeSpan.Zero);
			var header = Encode(new Dictionary<string, object> { { "alg", "RS256" }, { "kid", KeyId }, { "typ", "JWT" } });
			var payload = Encode(new Dictionary<string, object>
			{
				{ "iss", issuer }, { "aud", audience }, { "sub", "1234567890" }, { "email", email }, { "email_verified", emailVerified },
				{ "iat", issuedAt.ToUnixTimeSeconds() }, { "exp", issuedAt.AddHours(1).ToUnixTimeSeconds() }
			});
			var signature = _key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			return header + "." + payload + "." + Base64Url.EncodeToString(signature);
		}

		/// <summary>DeveloperNotification 을 Pub/Sub 푸시 본문으로 감쌉니다.</summary>
		public static string Push(object notification)
		{
			var data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(notification));
			return JsonSerializer.Serialize(new
			{
				message = new { data, messageId = Guid.NewGuid().ToString("N"), publishTime = "2026-10-15T00:00:00Z" },
				subscription = "projects/test/subscriptions/rtdn"
			});
		}

		private static string Encode(Dictionary<string, object> value)
		{
			return Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));
		}

		public async ValueTask DisposeAsync()
		{
			_key.Dispose();
			await _app.StopAsync();
			await _app.DisposeAsync();
		}
	}
}
