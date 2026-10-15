using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	/// <summary>결제 서버 등록 확장입니다.</summary>
	public static class IapServerExtensions
	{
		/// <summary>
		/// 지급 처리(<see cref="IPurchaseRedeemer"/>, 요청 범위)와 설정을 등록합니다. 돌려준 빌더로 검증기, 지급자, 원장을 붙입니다.
		/// 상품 목록이 비었거나 ID 가 겹치면 시작할 때 실패합니다.
		/// </summary>
		public static IapServerBuilder AddGeunedaIap(this IServiceCollection services, Action<IapServerOptions> configure)
		{
			if (services == null)
			{
				throw new ArgumentNullException(nameof(services));
			}

			if (configure == null)
			{
				throw new ArgumentNullException(nameof(configure));
			}

			services.AddOptions<IapServerOptions>()
				.Configure(configure)
				.Validate(o => IapProductCatalog.Validate(o) == null,
					"IapServerOptions is invalid (empty, null or duplicate products, MaxRequestBytes < 1 or a non-positive AcknowledgementRetryInterval)")
				.ValidateOnStart();
			services.TryAddSingleton(TimeProvider.System);
			services.TryAddSingleton<IapProductCatalog>();
			services.TryAddSingleton<StoreVerifierRegistry>();
			services.TryAddScoped<IPurchaseRedeemer, PurchaseRedeemer>();
			return new IapServerBuilder(services);
		}

		/// <summary>
		/// <c>POST {prefix}/purchases</c> 지급 엔드포인트를 붙입니다. 요청 수 제한 등은 돌려준 빌더에 붙입니다.
		/// 검증기·지급자·원장이 없거나 검증기가 같은 스토어를 겹쳐 받거나 가짜 스토어를 Production 에 붙였으면 여기서 실패합니다.
		/// </summary>
		/// <param name="resolveAccountId">
		/// 요청의 계정 ID 를 돌려줍니다(account 패키지면 <c>IAccessTokenValidator</c> 로 Bearer 토큰 검증). 인증하지 못하면 null 을 돌려주고, 엔드포인트가 401 로 답합니다
		/// </param>
		public static RouteHandlerBuilder MapGeunedaIap(this IEndpointRouteBuilder endpoints, string prefix, Func<HttpContext, Task<string>> resolveAccountId)
		{
			if (endpoints == null)
			{
				throw new ArgumentNullException(nameof(endpoints));
			}

			if (prefix == null)
			{
				throw new ArgumentNullException(nameof(prefix));
			}

			if (resolveAccountId == null)
			{
				throw new ArgumentNullException(nameof(resolveAccountId));
			}

			var services = endpoints.ServiceProvider;
			var registry = services.GetRequiredService<IServiceProviderIsService>();

			if (!registry.IsService(typeof(IPurchaseRedeemer)))
			{
				throw new InvalidOperationException("Call AddGeunedaIap before MapGeunedaIap");
			}

			if (!registry.IsService(typeof(IPurchaseLedger)))
			{
				throw new InvalidOperationException("Register a purchase ledger (AddLedger or AddInMemoryLedger) before MapGeunedaIap");
			}

			if (!registry.IsService(typeof(IPurchaseGrantor)))
			{
				throw new InvalidOperationException("Register a purchase grantor (AddGrantor) before MapGeunedaIap");
			}

			// 검증기를 지금 만들어 설정 오류(겹친 스토어, Production 의 가짜 스토어, 잘못된 서비스 계정 키)를 시작할 때 드러낸다
			if (services.GetRequiredService<StoreVerifierRegistry>().Count == 0)
			{
				throw new InvalidOperationException("Add a store verifier (AddGooglePlay, AddAppStore, AddFakeStore or AddVerifier) before MapGeunedaIap");
			}

			Func<HttpContext, Task<IResult>> handler = context => IapRedeemEndpoint.HandleAsync(context, resolveAccountId);
			return endpoints.MapPost(prefix.TrimEnd('/') + "/" + IapRoutes.Purchases, handler);
		}

		/// <summary>
		/// Google Play 실시간 개발자 알림 엔드포인트 <c>POST {prefix}/notifications/google-play</c> 를 붙입니다. Pub/Sub 푸시 구독의 엔드포인트로 이 주소를 넣고
		/// 인증(OIDC)을 켭니다. <see cref="IapServerBuilder.AddGooglePlay"/>, 원장, <see cref="IapServerBuilder.AddNotificationHandler{T}"/> 와
		/// <see cref="GooglePlayOptions.NotificationAudience"/>·<see cref="GooglePlayOptions.NotificationServiceAccountEmail"/> 가 필요합니다(없으면 여기서 예외).
		/// </summary>
		public static RouteHandlerBuilder MapGeunedaIapGooglePlayNotifications(this IEndpointRouteBuilder endpoints, string prefix)
		{
			RequireNotifications(endpoints, prefix, typeof(GooglePlayApi), "AddGooglePlay");
			var options = endpoints.ServiceProvider.GetRequiredService<IOptions<GooglePlayOptions>>().Value;

			if (string.IsNullOrWhiteSpace(options.NotificationAudience) || string.IsNullOrWhiteSpace(options.NotificationServiceAccountEmail))
			{
				throw new InvalidOperationException("Set GooglePlayOptions.NotificationAudience and NotificationServiceAccountEmail to receive Google Play notifications");
			}

			Func<HttpContext, Task<IResult>> handler = IapNotificationEndpoints.HandleGooglePlayAsync;
			return endpoints.MapPost(prefix.TrimEnd('/') + "/" + GooglePlayNotificationsRoute, handler);
		}

		/// <summary>
		/// App Store Server Notifications V2 엔드포인트 <c>POST {prefix}/notifications/app-store</c> 를 붙입니다. App Store Connect 의 앱 정보에서 운영·샌드박스 서버 URL 로 넣고
		/// 버전 2 를 고릅니다. <see cref="IapServerBuilder.AddAppStore"/>, 원장, <see cref="IapServerBuilder.AddNotificationHandler{T}"/> 가 필요합니다(없으면 여기서 예외).
		/// </summary>
		public static RouteHandlerBuilder MapGeunedaIapAppStoreNotifications(this IEndpointRouteBuilder endpoints, string prefix)
		{
			RequireNotifications(endpoints, prefix, typeof(AppleSignedDataVerifier), "AddAppStore");
			Func<HttpContext, Task<IResult>> handler = IapNotificationEndpoints.HandleAppStoreAsync;
			return endpoints.MapPost(prefix.TrimEnd('/') + "/" + AppStoreNotificationsRoute, handler);
		}

		internal const string GooglePlayNotificationsRoute = "notifications/google-play";
		internal const string AppStoreNotificationsRoute = "notifications/app-store";

		private static void RequireNotifications(IEndpointRouteBuilder endpoints, string prefix, Type store, string addStore)
		{
			if (endpoints == null)
			{
				throw new ArgumentNullException(nameof(endpoints));
			}

			if (prefix == null)
			{
				throw new ArgumentNullException(nameof(prefix));
			}

			var registry = endpoints.ServiceProvider.GetRequiredService<IServiceProviderIsService>();

			if (!registry.IsService(store))
			{
				throw new InvalidOperationException($"Call {addStore} before mapping its notifications");
			}

			if (!registry.IsService(typeof(IPurchaseLedger)))
			{
				throw new InvalidOperationException("Register a purchase ledger (AddLedger or AddInMemoryLedger) before mapping notifications");
			}

			if (!registry.IsService(typeof(IPurchaseNotificationHandler)))
			{
				throw new InvalidOperationException("Register a notification handler (AddNotificationHandler) before mapping notifications");
			}
		}
	}
}
