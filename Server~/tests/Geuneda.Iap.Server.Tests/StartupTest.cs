using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>설정 오류가 첫 요청이 아니라 시작할 때(<c>MapGeunedaIap</c>) 드러나는지 확인합니다.</summary>
	public sealed class StartupTest
	{
		private static WebApplication Build(Action<IapServerBuilder> build, Action<IapServerOptions> configure = null, string environment = "Development",
			Action<IServiceCollection> services = null)
		{
			var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment });
			builder.Logging.ClearProviders();
			services?.Invoke(builder.Services);
			build(builder.Services.AddGeunedaIap(configure ?? (options => options.Products.Add(new IapServerProduct("gem_100", IapProductType.Consumable)))));
			return builder.Build();
		}

		private static void Map(WebApplication app)
		{
			app.MapGeunedaIap("/iap", IapTestServer.ResolveAccount);
		}

		[Fact]
		public void Missing_ledger_grantor_or_verifier_fails()
		{
			// 개발 환경은 Build 의 DI 검사가 먼저 실패하므로, 그 검사가 없는 Production 에서 MapGeunedaIap 의 검사를 확인한다
			var noLedger = Build(iap => iap.AddVerifier<SecondFakeVerifier>().AddGrantor<TestGrantor>(), environment: "Production");
			var noGrantor = Build(iap => iap.AddVerifier<SecondFakeVerifier>().AddInMemoryLedger(), environment: "Production");
			var noVerifier = Build(iap => iap.AddGrantor<TestGrantor>().AddInMemoryLedger(), environment: "Production");

			Assert.Contains("ledger", Assert.Throws<InvalidOperationException>(() => Map(noLedger)).Message);
			Assert.Contains("grantor", Assert.Throws<InvalidOperationException>(() => Map(noGrantor)).Message);
			Assert.Contains("verifier", Assert.Throws<InvalidOperationException>(() => Map(noVerifier)).Message);
		}

		[Fact]
		public void Fake_store_in_production_fails()
		{
			var app = Build(iap => iap.AddFakeStore().AddGrantor<TestGrantor>().AddInMemoryLedger(), environment: "Production");

			Assert.Contains("Production", Assert.Throws<InvalidOperationException>(() => Map(app)).Message);
		}

		[Fact]
		public void Two_verifiers_for_one_store_fail()
		{
			var app = Build(iap => iap.AddFakeStore().AddVerifier<SecondFakeVerifier>().AddGrantor<TestGrantor>().AddInMemoryLedger());

			Assert.Contains("fake", Assert.Throws<InvalidOperationException>(() => Map(app)).Message);
		}

		[Theory]
		[InlineData("empty")]
		[InlineData("duplicate")]
		[InlineData("duplicateStoreId")]
		[InlineData("maxBytes")]
		public void Invalid_products_fail(string kind)
		{
			var app = Build(iap => iap.AddFakeStore().AddGrantor<TestGrantor>().AddInMemoryLedger(), options =>
			{
				switch (kind)
				{
					case "duplicate":
						options.Products.Add(new IapServerProduct("gem_100", IapProductType.Consumable));
						options.Products.Add(new IapServerProduct("gem_100", IapProductType.Consumable, "other"));
						break;
					case "duplicateStoreId":
						options.Products.Add(new IapServerProduct("gem_100", IapProductType.Consumable, "gem"));
						options.Products.Add(new IapServerProduct("gem_200", IapProductType.Consumable, "gem"));
						break;
					case "maxBytes":
						options.Products.Add(new IapServerProduct("gem_100", IapProductType.Consumable));
						options.MaxRequestBytes = 0;
						break;
				}
			});

			Assert.Throws<OptionsValidationException>(() => app.Services.GetRequiredService<IOptions<IapServerOptions>>().Value);
		}

		[Fact]
		public void Missing_bundle_id_fails()
		{
			var app = Build(iap => iap.AddAppStore(_ => { }).AddGrantor<TestGrantor>().AddInMemoryLedger());

			Assert.Throws<OptionsValidationException>(() => Map(app));
		}

		[Fact]
		public void Invalid_service_account_key_fails()
		{
			var app = Build(iap => iap.AddGooglePlay(options =>
			{
				options.PackageName = "com.test.game";
				options.ServiceAccountKeyJson = "{\"type\":\"service_account\"}";
			}).AddGrantor<TestGrantor>().AddInMemoryLedger());

			Assert.Throws<InvalidOperationException>(() => Map(app));
		}

		[Fact]
		public void Notification_endpoints_need_their_store_handler_and_push_settings()
		{
			var noGoogle = Build(iap => iap.AddFakeStore().AddGrantor<TestGrantor>().AddInMemoryLedger().AddNotificationHandler<TestNotificationHandler>());
			var noHandler = Build(iap => iap.AddGooglePlay(options =>
			{
				options.PackageName = "com.test.game";
				options.NotificationAudience = "aud";
				options.NotificationServiceAccountEmail = "push@test";
			}).AddGrantor<TestGrantor>().AddInMemoryLedger(), services: services => services.AddSingleton<IGooglePlayAccessTokenSource>(new FakeTokenSource()));
			var noAudience = Build(iap => iap.AddGooglePlay(options => options.PackageName = "com.test.game").AddGrantor<TestGrantor>().AddInMemoryLedger()
				.AddNotificationHandler<TestNotificationHandler>(), services: services => services.AddSingleton<IGooglePlayAccessTokenSource>(new FakeTokenSource()));
			var noAppStore = Build(iap => iap.AddFakeStore().AddGrantor<TestGrantor>().AddInMemoryLedger().AddNotificationHandler<TestNotificationHandler>());

			Assert.Contains("AddGooglePlay", Assert.Throws<InvalidOperationException>(() => noGoogle.MapGeunedaIapGooglePlayNotifications("/iap")).Message);
			Assert.Contains("handler", Assert.Throws<InvalidOperationException>(() => noHandler.MapGeunedaIapGooglePlayNotifications("/iap")).Message);
			Assert.Contains("NotificationAudience", Assert.Throws<InvalidOperationException>(() => noAudience.MapGeunedaIapGooglePlayNotifications("/iap")).Message);
			Assert.Contains("AddAppStore", Assert.Throws<InvalidOperationException>(() => noAppStore.MapGeunedaIapAppStoreNotifications("/iap")).Message);
		}

		[Fact]
		public void Products_validate_their_arguments()
		{
			Assert.Throws<ArgumentException>(() => new IapServerProduct(" ", IapProductType.Consumable));
			Assert.Throws<ArgumentOutOfRangeException>(() => new IapServerProduct("gem", (IapProductType) 9));
			Assert.Throws<ArgumentException>(() => new IapServerProduct("gem", IapProductType.Consumable, " "));
			Assert.Throws<ArgumentException>(() => new IapServerProduct("gem", IapProductType.Consumable, appStoreId: ""));
			Assert.Equal("gem", new IapServerProduct("gem", IapProductType.Consumable).AppStoreId);
		}

		private sealed class SecondFakeVerifier : IStoreVerifier
		{
			public IReadOnlyCollection<string> Stores { get; } = new[] { IapStoreNames.Fake };

			public string LedgerStore => IapStoreNames.Fake;

			public Task<StoreVerification> VerifyAsync(IapRedeemRequest request, IapServerProduct product, CancellationToken cancellationToken)
			{
				throw new NotSupportedException();
			}

			public Task<bool> AcknowledgeAsync(PurchaseRecord record, IapServerProduct product, CancellationToken cancellationToken)
			{
				return Task.FromResult(true);
			}
		}
	}
}
