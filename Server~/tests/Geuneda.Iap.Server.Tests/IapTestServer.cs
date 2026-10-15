using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>
	/// 127.0.0.1 빈 포트에 띄운 결제 서버입니다. 계정은 <see cref="AccountHeader"/> 헤더로 정합니다. 상품은 <c>gem_100</c>(소모성), <c>no_ads</c>(비소모성),
	/// <c>vip_month</c>(구독)이고, App Store 상품 ID 는 <c>com.test.</c> 접두사입니다.
	/// </summary>
	internal sealed class IapTestServer : IAsyncDisposable
	{
		public const string AccountHeader = "X-Test-Account";
		public const string DefaultAccount = "account-1";
		public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

		private readonly WebApplication _app;

		private IapTestServer(WebApplication app, FakeTimeProvider time, Uri baseAddress)
		{
			_app = app;
			Time = time;
			Http = new HttpClient { BaseAddress = baseAddress };
		}

		public FakeTimeProvider Time { get; }
		public HttpClient Http { get; }
		public IServiceProvider Services => _app.Services;
		public TestGrantor Grantor => (TestGrantor) _app.Services.GetRequiredService<IPurchaseGrantor>();
		public InMemoryPurchaseLedger Ledger => _app.Services.GetRequiredService<InMemoryPurchaseLedger>();

		public static async Task<IapTestServer> StartAsync(Action<IapServerBuilder> stores, Action<IapServerOptions> configure = null,
			Action<IServiceCollection> services = null)
		{
			var time = new FakeTimeProvider(Now);
			var app = Build(time, stores, configure, services, "Development");
			app.MapGeunedaIap("/iap", ResolveAccount);
			await app.StartAsync();

			var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
			return new IapTestServer(app, time, new Uri(address + "/iap/"));
		}

		public static WebApplication Build(TimeProvider time, Action<IapServerBuilder> stores, Action<IapServerOptions> configure,
			Action<IServiceCollection> services, string environment)
		{
			var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment });
			builder.WebHost.UseUrls("http://127.0.0.1:0");
			builder.Logging.ClearProviders();
			builder.Services.AddSingleton(time);
			services?.Invoke(builder.Services);
			var iap = builder.Services.AddGeunedaIap(options =>
			{
				options.Products.Add(new IapServerProduct("gem_100", IapProductType.Consumable, appStoreId: "com.test.gem100"));
				options.Products.Add(new IapServerProduct("no_ads", IapProductType.NonConsumable, appStoreId: "com.test.noads"));
				options.Products.Add(new IapServerProduct("vip_month", IapProductType.Subscription, appStoreId: "com.test.vip.month"));
				configure?.Invoke(options);
			});
			iap.AddGrantor<TestGrantor>(ServiceLifetime.Singleton).AddInMemoryLedger();
			stores(iap);
			return builder.Build();
		}

		public static Task<string> ResolveAccount(HttpContext context)
		{
			var account = context.Request.Headers[AccountHeader].ToString();
			return Task.FromResult(string.IsNullOrEmpty(account) ? null : account);
		}

		public static IapRedeemRequest Request(string store, string productId, string transactionId, string signedTransaction = null, string payload = null)
		{
			return new IapRedeemRequest
			{
				Store = store,
				ProductId = productId,
				TransactionId = transactionId,
				Receipt = "{}",
				SignedTransaction = signedTransaction,
				Payload = payload
			};
		}

		public async Task<(int Status, JsonElement Body)> RedeemAsync(IapRedeemRequest request, string account = DefaultAccount)
		{
			using (var message = new HttpRequestMessage(HttpMethod.Post, IapRoutes.Purchases) { Content = JsonContent.Create(request) })
			{
				if (account != null)
				{
					message.Headers.Add(AccountHeader, account);
				}

				using (var response = await Http.SendAsync(message))
				{
					// 500 은 개발자 예외 페이지(JSON 아님)라 본문을 읽지 않는다
					var json = response.Content.Headers.ContentType?.MediaType == "application/json";
					var body = json ? JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()) : default;
					return ((int) response.StatusCode, body);
				}
			}
		}

		public async ValueTask DisposeAsync()
		{
			Http.Dispose();
			await _app.StopAsync();
			await _app.DisposeAsync();
		}
	}

	internal static class RedeemBody
	{
		public static IapRedeemStatus Status(this JsonElement body)
		{
			return (IapRedeemStatus) body.GetProperty("status").GetInt32();
		}

		public static int Code(this JsonElement body)
		{
			return body.GetProperty("code").GetInt32();
		}

		public static bool Has(this JsonElement body, string name)
		{
			return body.TryGetProperty(name, out _);
		}
	}
}
