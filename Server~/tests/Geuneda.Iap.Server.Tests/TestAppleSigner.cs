using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>
	/// Apple 과 같은 모양(루트 → 중간 → 잎, ECDSA P-256, Apple OID)의 테스트 인증서로 App Store JWS 를 만듭니다. 테스트 서버에는 <see cref="RootDer"/> 를 신뢰 루트로 넣습니다.
	/// </summary>
	internal sealed class TestAppleSigner : IDisposable
	{
		public const string BundleId = "com.test.game";
		public static readonly DateTimeOffset NotBefore = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
		public static readonly DateTimeOffset NotAfter = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

		private readonly X509Certificate2 _root;
		private readonly X509Certificate2 _intermediate;
		private readonly X509Certificate2 _leaf;
		private readonly ECDsa _leafKey;

		private TestAppleSigner(X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf, ECDsa leafKey)
		{
			_root = root;
			_intermediate = intermediate;
			_leaf = leaf;
			_leafKey = leafKey;
		}

		public byte[] RootDer => _root.RawData;

		/// <param name="leafOid">잎 인증서에 App Store 서명 OID 를 넣을지</param>
		/// <param name="intermediateOid">중간 인증서에 WWDR OID 를 넣을지</param>
		/// <param name="leafNotAfter">잎 인증서 만료(기본 2030년)</param>
		/// <param name="leafAiaUrl">있으면 잎 인증서의 발급자 내려받기(caIssuers)·OCSP 주소</param>
		public static TestAppleSigner Create(bool leafOid = true, bool intermediateOid = true, DateTimeOffset? leafNotAfter = null, string leafAiaUrl = null)
		{
			using (var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
			using (var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
			{
				var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
				var rootRequest = new CertificateRequest("CN=Test Apple Root", rootKey, HashAlgorithmName.SHA256);
				rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
				rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
				rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
				var root = rootRequest.CreateSelfSigned(NotBefore, NotAfter);

				var intermediateRequest = new CertificateRequest("CN=Test Apple WWDR", intermediateKey, HashAlgorithmName.SHA256);
				intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
				intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
				intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));
				intermediateRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));

				if (intermediateOid)
				{
					intermediateRequest.CertificateExtensions.Add(new X509Extension("1.2.840.113635.100.6.2.1", new byte[] { 0x05, 0x00 }, false));
				}

				using (var intermediatePublic = intermediateRequest.Create(root, NotBefore, NotAfter, new byte[] { 1, 2, 3, 4 }))
				{
					var intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);
					var leafRequest = new CertificateRequest("CN=Test App Store Signing", leafKey, HashAlgorithmName.SHA256);
					leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
					leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
					leafRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(intermediate, true, false));

					if (leafOid)
					{
						leafRequest.CertificateExtensions.Add(new X509Extension("1.2.840.113635.100.6.11.1", new byte[] { 0x05, 0x00 }, false));
					}

					if (leafAiaUrl != null)
					{
						leafRequest.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(new[] { leafAiaUrl + "/ocsp" }, new[] { leafAiaUrl + "/issuer.cer" }));
					}

					var leaf = leafRequest.Create(intermediate, NotBefore, leafNotAfter ?? NotAfter, new byte[] { 5, 6, 7, 8 });
					return new TestAppleSigner(root, intermediate, leaf, leafKey);
				}
			}
		}

		/// <summary>거래 페이로드를 JWS 로 서명합니다. <paramref name="chain"/> 을 주면 그 인증서 목록을 x5c 로 넣습니다.</summary>
		public string Sign(object payload, string algorithm = "ES256", IReadOnlyList<X509Certificate2> chain = null)
		{
			var certificates = new List<string>();

			foreach (var certificate in chain ?? new[] { _leaf, _intermediate, _root })
			{
				certificates.Add(Convert.ToBase64String(certificate.RawData));
			}

			var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { { "alg", algorithm }, { "x5c", certificates } }));
			var body = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
			var signature = _leafKey.SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256);
			return header + "." + body + "." + Base64Url.EncodeToString(signature);
		}

		public X509Certificate2 Leaf => _leaf;
		public X509Certificate2 Intermediate => _intermediate;
		public X509Certificate2 Root => _root;

		/// <summary>App Store 거래 페이로드(JWSTransactionDecodedPayload 모양)입니다.</summary>
		public static Dictionary<string, object> Transaction(string transactionId, string productId, string environment = "Production",
			string bundleId = BundleId, long? revocationDate = null, long? expiresDate = null, long signedDate = 1_760_000_000_000)
		{
			var payload = new Dictionary<string, object>
			{
				{ "transactionId", transactionId },
				{ "originalTransactionId", "1000000000000001" },
				{ "bundleId", bundleId },
				{ "productId", productId },
				{ "purchaseDate", 1_760_000_000_000L },
				{ "quantity", 1 },
				{ "type", "Consumable" },
				{ "environment", environment },
				{ "appAccountToken", "7e3f5b4a-1111-2222-3333-444455556666" },
				{ "price", 1_200_000L },
				{ "currency", "KRW" },
				{ "storefront", "KOR" },
				{ "signedDate", signedDate }
			};

			if (revocationDate != null)
			{
				payload["revocationDate"] = revocationDate.Value;
				payload["revocationReason"] = 0;
			}

			if (expiresDate != null)
			{
				payload["expiresDate"] = expiresDate.Value;
				payload["type"] = "Auto-Renewable Subscription";
			}

			return payload;
		}

		public void Dispose()
		{
			_root.Dispose();
			_intermediate.Dispose();
			_leaf.Dispose();
			_leafKey.Dispose();
		}
	}
}
