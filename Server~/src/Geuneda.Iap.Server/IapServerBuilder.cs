using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	/// <summary><see cref="IapServerExtensions.AddGeunedaIap"/> 가 돌려주는 등록 빌더입니다. 검증기, 지급자, 원장을 붙입니다.</summary>
	public sealed class IapServerBuilder
	{
		internal IapServerBuilder(IServiceCollection services)
		{
			Services = services;
		}

		public IServiceCollection Services { get; }

		/// <summary>
		/// Google Play 검증기를 붙입니다. <see cref="GooglePlayOptions.PackageName"/> 이 비면 시작할 때 실패합니다.
		/// <see cref="IGooglePlayAccessTokenSource"/> 를 먼저 등록했으면 그것을 씁니다(아니면 서비스 계정·ADC).
		/// </summary>
		public IapServerBuilder AddGooglePlay(Action<GooglePlayOptions> configure)
		{
			if (configure == null)
			{
				throw new ArgumentNullException(nameof(configure));
			}

			Services.AddOptions<GooglePlayOptions>()
				.Configure(configure)
				.Validate(o => !string.IsNullOrWhiteSpace(o.PackageName), "GooglePlayOptions.PackageName is required")
				.Validate(o => o.RequestTimeout > TimeSpan.Zero, "GooglePlayOptions.RequestTimeout must be positive")
				.Validate(o => o.ApiBaseAddress != null && o.ApiBaseAddress.IsAbsoluteUri && o.ApiBaseAddress.AbsoluteUri.EndsWith('/'),
					"GooglePlayOptions.ApiBaseAddress must be absolute and end with /")
				.ValidateOnStart();
			Services.AddHttpClient(GooglePlayApi.HttpClientName, (provider, client) =>
			{
				var options = provider.GetRequiredService<IOptions<GooglePlayOptions>>().Value;
				client.BaseAddress = options.ApiBaseAddress;
				client.Timeout = options.RequestTimeout;
			});
			Services.TryAddSingleton<IGooglePlayAccessTokenSource, GoogleCredentialAccessTokenSource>();
			Services.TryAddSingleton<GooglePlayApi>();
			// 지급 뒤 확인을 못 한 거래(확인 실패, 그 사이 서버 종료)를 원장에서 찾아 다시 확인한다
			Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PendingAcknowledgementWorker>());
			return AddVerifier<GooglePlayVerifier>();
		}

		/// <summary>App Store·Mac App Store 검증기를 붙입니다. <see cref="AppStoreOptions.BundleId"/> 가 비면 시작할 때 실패합니다.</summary>
		public IapServerBuilder AddAppStore(Action<AppStoreOptions> configure)
		{
			if (configure == null)
			{
				throw new ArgumentNullException(nameof(configure));
			}

			Services.AddOptions<AppStoreOptions>()
				.Configure(configure)
				.Validate(o => !string.IsNullOrWhiteSpace(o.BundleId), "AppStoreOptions.BundleId is required")
				.ValidateOnStart();
			Services.TryAddSingleton<AppleSignedDataVerifier>();
			return AddVerifier<AppStoreVerifier>();
		}

		/// <summary>Unity IAP 에디터 가짜 스토어 주문을 검증 없이 받습니다. 개발 서버 전용이며 호스트 환경이 Production 이면 <c>MapGeunedaIap</c> 에서 실패합니다.</summary>
		public IapServerBuilder AddFakeStore()
		{
			return AddVerifier<FakeStoreVerifier>();
		}

		/// <summary>다른 스토어의 검증기를 붙입니다(싱글턴). 두 검증기가 같은 스토어 이름을 받으면 <c>MapGeunedaIap</c> 에서 실패합니다.</summary>
		public IapServerBuilder AddVerifier<T>() where T : class, IStoreVerifier
		{
			Services.TryAddEnumerable(ServiceDescriptor.Singleton<IStoreVerifier, T>());
			return this;
		}

		/// <summary>게임 지급자를 붙입니다. 원장과 같은 DB 트랜잭션을 쓰도록 기본 수명은 요청 범위입니다.</summary>
		public IapServerBuilder AddGrantor<T>(ServiceLifetime lifetime = ServiceLifetime.Scoped) where T : class, IPurchaseGrantor
		{
			Services.Replace(ServiceDescriptor.Describe(typeof(IPurchaseGrantor), typeof(T), lifetime));
			return this;
		}

		/// <summary>원장을 붙입니다. 기본 수명은 요청 범위입니다.</summary>
		public IapServerBuilder AddLedger<T>(ServiceLifetime lifetime = ServiceLifetime.Scoped) where T : class, IPurchaseLedger
		{
			Services.Replace(ServiceDescriptor.Describe(typeof(IPurchaseLedger), typeof(T), lifetime));
			return this;
		}

		/// <summary>메모리 원장(<see cref="InMemoryPurchaseLedger"/>, 싱글턴)을 붙입니다. 개발·테스트용입니다.</summary>
		public IapServerBuilder AddInMemoryLedger()
		{
			Services.TryAddSingleton<InMemoryPurchaseLedger>();
			Services.Replace(ServiceDescriptor.Singleton<IPurchaseLedger>(provider => provider.GetRequiredService<InMemoryPurchaseLedger>()));
			return this;
		}
	}
}
