using System;
using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>App Store 검증기(StoreKit 2 JWS)를 Apple 과 같은 모양의 테스트 인증서로 확인합니다.</summary>
	public sealed class AppStoreTest : IDisposable
	{
		private readonly TestAppleSigner _signer = TestAppleSigner.Create();

		public void Dispose()
		{
			_signer.Dispose();
		}

		private Task<IapTestServer> StartAsync(Action<AppStoreOptions> configure = null, TestAppleSigner signer = null)
		{
			return IapTestServer.StartAsync(iap => iap.AddAppStore(options =>
			{
				options.BundleId = TestAppleSigner.BundleId;
				options.OnlineChecks = false;
				options.RootCertificates.Add((signer ?? _signer).RootDer);
				configure?.Invoke(options);
			}));
		}

		private static IapRedeemRequest Request(string jws, string transactionId = "2000000123", string productId = "gem_100",
			string store = IapStoreNames.AppleAppStore)
		{
			return IapTestServer.Request(store, productId, transactionId, jws);
		}

		[Fact]
		public async Task Signed_transaction_is_granted_with_the_paid_amount()
		{
			await using var server = await StartAsync();
			var jws = _signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem100"));

			var (_, body) = await server.RedeemAsync(Request(jws));

			Assert.Equal(IapRedeemStatus.Granted, body.Status());
			Assert.Equal(1200m, body.GetProperty("paidAmount").GetDecimal());
			Assert.Equal("KRW", body.GetProperty("paidCurrency").GetString());
			Assert.False(body.GetProperty("isTest").GetBoolean());

			var purchase = Assert.Single(server.Grantor.Grants).Purchase;
			Assert.Equal("com.test.gem100", purchase.StoreProductId);
			Assert.Equal("1000000000000001", purchase.OriginalTransactionId);
			Assert.Equal("7e3f5b4a-1111-2222-3333-444455556666", purchase.AccountToken);
			Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000), purchase.PurchasedAt);
		}

		[Fact]
		public async Task Mac_app_store_shares_the_ledger_with_the_app_store()
		{
			await using var server = await StartAsync();
			var jws = _signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem100"));

			var (_, mac) = await server.RedeemAsync(Request(jws, store: IapStoreNames.MacAppStore));
			var (_, ios) = await server.RedeemAsync(Request(jws));

			Assert.Equal(IapRedeemStatus.Granted, mac.Status());
			Assert.Equal(IapRedeemCodes.Duplicate, ios.Code());
			Assert.Equal(1, server.Grantor.Calls);
			Assert.NotNull(await server.Ledger.FindAsync(IapStoreNames.AppleAppStore, "2000000123", System.Threading.CancellationToken.None));
		}

		[Fact]
		public async Task Sandbox_is_a_test_purchase_unless_disallowed()
		{
			var jws = _signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem100", "Sandbox"));

			await using (var server = await StartAsync())
			{
				var (_, body) = await server.RedeemAsync(Request(jws));
				Assert.Equal(IapRedeemStatus.Granted, body.Status());
				Assert.True(body.GetProperty("isTest").GetBoolean());
			}

			await using (var server = await StartAsync(options => options.AllowSandbox = false))
			{
				var (_, body) = await server.RedeemAsync(Request(jws));
				Assert.Equal(IapRedeemCodes.EnvironmentNotAllowed, body.Code());
			}
		}

		[Theory]
		[InlineData("Xcode")]
		[InlineData("LocalTesting")]
		[InlineData(null)]
		public async Task Other_environments_are_not_allowed(string environment)
		{
			await using var server = await StartAsync();
			var jws = _signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem100", environment));

			var (_, body) = await server.RedeemAsync(Request(jws));

			Assert.Equal(IapRedeemStatus.Rejected, body.Status());
			Assert.Equal(IapRedeemCodes.EnvironmentNotAllowed, body.Code());
		}

		[Fact]
		public async Task Other_bundle_transaction_and_product_are_rejected()
		{
			await using var server = await StartAsync();

			var (_, bundle) = await server.RedeemAsync(Request(_signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem100", bundleId: "com.other"))));
			var (_, transaction) = await server.RedeemAsync(Request(_signer.Sign(TestAppleSigner.Transaction("2000000999", "com.test.gem100"))));
			var (_, product) = await server.RedeemAsync(Request(_signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem500"))));

			Assert.Equal(IapRedeemCodes.AppMismatch, bundle.Code());
			Assert.Equal(IapRedeemCodes.InvalidReceipt, transaction.Code());
			Assert.Equal(IapRedeemCodes.ProductMismatch, product.Code());
			Assert.Equal(0, server.Grantor.Calls);
		}

		[Fact]
		public async Task Revoked_transaction_is_closed_without_a_grant()
		{
			await using var server = await StartAsync();
			var jws = _signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem100", revocationDate: 1_760_000_100_000));

			var (_, body) = await server.RedeemAsync(Request(jws));

			Assert.Equal(IapRedeemStatus.AlreadyGranted, body.Status());
			Assert.Equal(IapRedeemCodes.Revoked, body.Code());
			Assert.Equal(0, server.Grantor.Calls);
		}

		[Fact]
		public async Task Subscription_carries_expiry_and_state()
		{
			await using var server = await StartAsync();
			var expires = IapTestServer.Now.AddDays(30).ToUnixTimeMilliseconds();
			var expired = IapTestServer.Now.AddDays(-1).ToUnixTimeMilliseconds();

			await server.RedeemAsync(Request(_signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.vip.month", expiresDate: expires)),
				productId: "vip_month"));
			await server.RedeemAsync(Request(_signer.Sign(TestAppleSigner.Transaction("2000000124", "com.test.vip.month", expiresDate: expired)),
				"2000000124", "vip_month"));

			var grants = server.Grantor.Grants.ToArray();
			Assert.Equal(IapSubscriptionState.Active, grants[0].Purchase.SubscriptionState);
			Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(expires), grants[0].Purchase.ExpiresAt);
			Assert.Equal(IapSubscriptionState.Expired, grants[1].Purchase.SubscriptionState);
		}

		[Fact]
		public async Task Tampered_payload_and_wrong_algorithm_are_invalid()
		{
			await using var server = await StartAsync();
			var jws = _signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem100"));
			var parts = jws.Split('.');
			var forged = Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(
				System.Text.Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1])).Replace("com.test.gem100", "com.test.noads")));

			var (_, tampered) = await server.RedeemAsync(Request(parts[0] + "." + forged + "." + parts[2], productId: "no_ads"));
			var (_, algorithm) = await server.RedeemAsync(Request(_signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem100"), "RS256")));
			var (_, garbage) = await server.RedeemAsync(Request("not.a.jws"));
			var (_, missing) = await server.RedeemAsync(Request(null));

			Assert.All(new[] { tampered, algorithm, garbage, missing }, body =>
			{
				Assert.Equal(IapRedeemStatus.Rejected, body.Status());
				Assert.Equal(IapRedeemCodes.InvalidReceipt, body.Code());
			});
		}

		[Fact]
		public async Task Untrusted_root_short_chain_and_missing_oids_are_invalid()
		{
			using var stranger = TestAppleSigner.Create();
			using var noLeafOid = TestAppleSigner.Create(leafOid: false);
			using var noIntermediateOid = TestAppleSigner.Create(intermediateOid: false);
			await using var server = await StartAsync();
			var payload = TestAppleSigner.Transaction("2000000123", "com.test.gem100");

			var (_, untrusted) = await server.RedeemAsync(Request(stranger.Sign(payload)));
			var (_, shortChain) = await server.RedeemAsync(Request(_signer.Sign(payload, chain: new[] { _signer.Leaf, _signer.Intermediate })));
			var (_, leafOid) = await server.RedeemAsync(Request(noLeafOid.Sign(payload)));
			var (_, intermediateOid) = await server.RedeemAsync(Request(noIntermediateOid.Sign(payload)));

			Assert.All(new[] { untrusted, shortChain, leafOid, intermediateOid }, body => Assert.Equal(IapRedeemCodes.InvalidReceipt, body.Code()));
		}

		[Fact]
		public async Task Chain_through_another_intermediate_is_invalid()
		{
			// 신뢰 루트 아래의 다른 중간 인증서로 서명하고 x5c 에는 원래 중간 인증서를 넣은 경우(서명 키가 체인과 맞지 않음)
			using var other = TestAppleSigner.Create();
			await using var server = await StartAsync();
			var payload = TestAppleSigner.Transaction("2000000123", "com.test.gem100");

			var (_, body) = await server.RedeemAsync(Request(other.Sign(payload, chain: new[] { other.Leaf, _signer.Intermediate, _signer.Root })));

			Assert.Equal(IapRedeemCodes.InvalidReceipt, body.Code());
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task Untrusted_chain_does_not_make_the_server_fetch_its_urls(bool onlineChecks)
		{
			// 공격자는 Apple OID 를 넣은 자체 인증서로 JWS 서명을 통과하고, 발급자를 찾지 못하게 해 AIA·OCSP 주소(내부망)로 서버가 요청하게 만들 수 있다(SSRF)
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var connections = 0;
			using var stop = new CancellationTokenSource();
			var accepting = Task.Run(async () =>
			{
				while (!stop.IsCancellationRequested)
				{
					using var client = await listener.AcceptTcpClientAsync(stop.Token);
					Interlocked.Increment(ref connections);
				}
			});

			try
			{
				var url = $"http://127.0.0.1:{((IPEndPoint) listener.LocalEndpoint).Port}";
				using var attacker = TestAppleSigner.Create(leafAiaUrl: url);
				await using var server = await StartAsync(options => options.OnlineChecks = onlineChecks);
				var payload = TestAppleSigner.Transaction("2000000123", "com.test.gem100");

				var (_, body) = await server.RedeemAsync(Request(attacker.Sign(payload, chain: new[] { attacker.Leaf, _signer.Intermediate, _signer.Root })));

				Assert.Equal(IapRedeemCodes.InvalidReceipt, body.Code());
				Assert.Equal(0, Volatile.Read(ref connections));
			}
			finally
			{
				stop.Cancel();
				listener.Stop();
				await Assert.ThrowsAnyAsync<Exception>(() => accepting);
			}
		}

		[Fact]
		public async Task Offline_checks_use_the_signed_date_and_online_checks_use_now()
		{
			// 잎 인증서는 2026-01-01 에 만료되고 거래는 그 전에 서명됨(테스트 서버 시각은 2026-10-15)
			using var expiring = TestAppleSigner.Create(leafNotAfter: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
			var jws = expiring.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem100"));

			await using (var offline = await StartAsync(signer: expiring))
			{
				var (_, body) = await offline.RedeemAsync(Request(jws));
				Assert.Equal(IapRedeemStatus.Granted, body.Status());
			}

			await using (var online = await StartAsync(options => options.OnlineChecks = true, expiring))
			{
				var (_, body) = await online.RedeemAsync(Request(jws));
				Assert.Equal(IapRedeemCodes.InvalidReceipt, body.Code());
			}
		}

		[Fact]
		public async Task Online_check_without_revocation_info_retries_later()
		{
			// 테스트 인증서에는 OCSP·CRL 주소가 없어 폐기 여부를 알 수 없다(Apple 의 RETRYABLE_VERIFICATION_FAILURE)
			await using var server = await StartAsync(options => options.OnlineChecks = true);
			var jws = _signer.Sign(TestAppleSigner.Transaction("2000000123", "com.test.gem100"));

			var (_, body) = await server.RedeemAsync(Request(jws));

			Assert.Equal(IapRedeemStatus.RetryLater, body.Status());
			Assert.Equal(IapRedeemCodes.StoreUnavailable, body.Code());
		}

		[Fact]
		public void Embedded_root_is_apple_root_ca_g3()
		{
			var fingerprint = Convert.ToHexString(SHA256.HashData(AppleRootCertificates.AppleRootCaG3));

			Assert.Equal("63343ABFB89A6A03EBB57E9B3F5FA7BE7C4F5C756F3017B3A8C488C3653E9179", fingerprint);
		}
	}
}
