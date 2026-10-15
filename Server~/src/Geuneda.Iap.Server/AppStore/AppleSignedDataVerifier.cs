using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	internal enum AppleSignedDataStatus
	{
		Valid = 1,
		Invalid = 2,

		/// <summary>OCSP 에 닿지 못해 폐기 여부를 모름(Apple 라이브러리의 RETRYABLE_VERIFICATION_FAILURE)</summary>
		Retryable = 3,
	}

	internal readonly struct AppleSignedData
	{
		public AppleSignedData(AppleSignedDataStatus status, byte[] payload, string message)
		{
			Status = status;
			Payload = payload;
			Message = message;
		}

		public AppleSignedDataStatus Status { get; }

		/// <summary>검증한 JWS 페이로드(JSON). 실패면 null</summary>
		public byte[] Payload { get; }

		public string Message { get; }
	}

	/// <summary>
	/// App Store 서명 데이터(JWS: 거래, 갱신 정보, 서버 알림)를 검증합니다. Apple App Store Server Library 의 <c>SignedDataVerifier</c> 와 같은 규칙입니다.
	/// 헤더 <c>alg</c> ES256, <c>x5c</c> 인증서 3개, 잎 인증서 OID 1.2.840.113635.100.6.11.1, 중간 인증서 OID 1.2.840.113635.100.6.2.1, 신뢰 루트까지 체인,
	/// 잎 인증서 키로 ES256 서명. 온라인 검사면 지금 시각 기준과 OCSP 폐기 확인, 아니면 페이로드의 <c>signedDate</c> 기준입니다.
	/// 체인 신뢰는 네트워크 없이 먼저 확인하고(인증서 내려받기 끔), 신뢰한 체인에만 OCSP 를 묻습니다(공격자 인증서의 주소로 요청하지 않게).
	/// </summary>
	internal sealed class AppleSignedDataVerifier
	{
		private const string LeafOid = "1.2.840.113635.100.6.11.1";
		private const string IntermediateOid = "1.2.840.113635.100.6.2.1";
		private const X509ChainStatusFlags RetryableFlags = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

		private readonly X509Certificate2Collection _roots = new X509Certificate2Collection();
		private readonly bool _onlineChecks;
		private readonly TimeProvider _time;

		public AppleSignedDataVerifier(IOptions<AppStoreOptions> options, TimeProvider time)
		{
			var settings = options.Value;
			var roots = settings.RootCertificates.Count == 0 ? new List<byte[]> { AppleRootCertificates.AppleRootCaG3 } : settings.RootCertificates;

			foreach (var root in roots)
			{
				_roots.Add(X509CertificateLoader.LoadCertificate(root));
			}

			_onlineChecks = settings.OnlineChecks;
			_time = time;
		}

		public AppleSignedData Verify(string jws)
		{
			if (string.IsNullOrEmpty(jws))
			{
				return Invalid("Signed data is missing");
			}

			var parts = jws.Split('.');

			if (parts.Length != 3)
			{
				return Invalid("Signed data is not a compact JWS");
			}

			try
			{
				var chain = ReadHeader(parts[0], out var headerError);

				if (chain == null)
				{
					return Invalid(headerError);
				}

				var payload = Base64Url.DecodeFromChars(parts[1]);
				var signature = Base64Url.DecodeFromChars(parts[2]);

				using (var leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(chain[0])))
				using (var intermediate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(chain[1])))
				{
					var signatureError = VerifySignature(leaf, parts, signature);

					if (signatureError != null)
					{
						return Invalid(signatureError);
					}

					if (leaf.Extensions[LeafOid] == null)
					{
						return Invalid("Leaf certificate is not an App Store signing certificate");
					}

					if (intermediate.Extensions[IntermediateOid] == null)
					{
						return Invalid("Intermediate certificate is not an Apple WWDR certificate");
					}

					return VerifyChain(leaf, intermediate, SignedDate(payload), payload);
				}
			}
			catch (FormatException e)
			{
				return Invalid($"Malformed signed data: {e.Message}");
			}
			catch (JsonException e)
			{
				return Invalid($"Malformed signed data JSON: {e.Message}");
			}
			catch (CryptographicException e)
			{
				return Invalid($"Malformed certificate: {e.Message}");
			}
		}

		/// <summary>헤더를 읽어 x5c 3개를 돌려줍니다. 맞지 않으면 null 과 이유입니다.</summary>
		private static string[] ReadHeader(string encoded, out string error)
		{
			using (var header = JsonDocument.Parse(Base64Url.DecodeFromChars(encoded)))
			{
				var root = header.RootElement;

				if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("alg", out var alg) || alg.ValueKind != JsonValueKind.String ||
				    alg.GetString() != "ES256")
				{
					error = "Signed data algorithm is not ES256";
					return null;
				}

				if (!root.TryGetProperty("x5c", out var x5c) || x5c.ValueKind != JsonValueKind.Array || x5c.GetArrayLength() != 3)
				{
					error = "Signed data must carry a certificate chain of 3";
					return null;
				}

				var chain = new string[3];

				for (var i = 0; i < 3; i++)
				{
					if (x5c[i].ValueKind != JsonValueKind.String)
					{
						error = "Signed data certificate is not a string";
						return null;
					}

					chain[i] = x5c[i].GetString();
				}

				error = null;
				return chain;
			}
		}

		private static string VerifySignature(X509Certificate2 leaf, string[] parts, byte[] signature)
		{
			using (var key = leaf.GetECDsaPublicKey())
			{
				if (key == null)
				{
					return "Leaf certificate has no EC key";
				}

				var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
				return signature.Length == 64 && key.VerifyData(signed, signature, HashAlgorithmName.SHA256) ? null : "Signature does not match";
			}
		}

		private AppleSignedData VerifyChain(X509Certificate2 leaf, X509Certificate2 intermediate, DateTimeOffset? signedDate, byte[] payload)
		{
			// 1단계: 네트워크 없이 신뢰 루트까지의 체인만 확인한다. 신뢰하지 않는 인증서의 AIA(발급자 내려받기)·OCSP 주소로 서버가 요청하지 않게 한다(SSRF)
			var trustTime = _onlineChecks || signedDate == null ? _time.GetUtcNow() : signedDate.Value;

			using (var chain = CreateChain(intermediate, X509RevocationMode.NoCheck, trustTime))
			{
				chain.ChainPolicy.DisableCertificateDownloads = true;

				if (!chain.Build(leaf))
				{
					return Invalid($"Certificate chain is not trusted: {Describe(chain, out _)}");
				}

				// 체인이 넘겨받은 중간 인증서를 거쳐 루트에서 끝나야 한다(잎 → 중간 → 루트 3단계)
				if (chain.ChainElements.Count != 3 || !chain.ChainElements[1].Certificate.RawDataMemory.Span.SequenceEqual(intermediate.RawDataMemory.Span))
				{
					return Invalid("Certificate chain does not go through the given intermediate");
				}
			}

			if (!_onlineChecks)
			{
				return new AppleSignedData(AppleSignedDataStatus.Valid, payload, null);
			}

			// 2단계: Apple 루트가 발급한 체인임을 확인한 뒤에만 OCSP 로 폐기를 확인한다(주소는 Apple 인증서의 것)
			using (var chain = CreateChain(intermediate, X509RevocationMode.Online, _time.GetUtcNow()))
			{
				if (!chain.Build(leaf))
				{
					var details = Describe(chain, out var flags);
					return flags != X509ChainStatusFlags.NoError && (flags & ~RetryableFlags) == 0
						? new AppleSignedData(AppleSignedDataStatus.Retryable, null, $"Certificate revocation status is unknown: {details}")
						: Invalid($"Certificate is revoked or not trusted: {details}");
				}
			}

			return new AppleSignedData(AppleSignedDataStatus.Valid, payload, null);
		}

		private X509Chain CreateChain(X509Certificate2 intermediate, X509RevocationMode revocation, DateTimeOffset time)
		{
			var chain = new X509Chain();
			var policy = chain.ChainPolicy;
			policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
			policy.CustomTrustStore.AddRange(_roots);
			policy.ExtraStore.Add(intermediate);
			policy.RevocationMode = revocation;
			policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
			policy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
			policy.VerificationTime = time.UtcDateTime;
			return chain;
		}

		private static string Describe(X509Chain chain, out X509ChainStatusFlags flags)
		{
			flags = X509ChainStatusFlags.NoError;
			var details = new StringBuilder();

			foreach (var status in chain.ChainStatus)
			{
				flags |= status.Status;
				details.Append(status.Status).Append(' ');
			}

			return details.ToString().TrimEnd();
		}

		/// <summary>페이로드의 <c>signedDate</c>(밀리초)입니다. 없거나 숫자가 아니면 null 입니다.</summary>
		private static DateTimeOffset? SignedDate(byte[] payload)
		{
			using (var document = JsonDocument.Parse(payload))
			{
				var root = document.RootElement;
				return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("signedDate", out var value) && value.ValueKind == JsonValueKind.Number &&
				       value.TryGetInt64(out var ms)
					? DateTimeOffset.FromUnixTimeMilliseconds(ms)
					: null;
			}
		}

		private static AppleSignedData Invalid(string message)
		{
			return new AppleSignedData(AppleSignedDataStatus.Invalid, null, message);
		}
	}
}
