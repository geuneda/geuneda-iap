using System.Threading.Tasks;
using Geuneda.Iap;
using Geuneda.Iap.SampleServer;
using Geuneda.Iap.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// 실행: dotnet run --project "Server~/samples/Geuneda.Iap.SampleServer" --no-launch-profile -- --urls http://127.0.0.1:5499
// Unity 상호 운용 테스트: GEUNEDA_IAP_INTEROP_URL=http://127.0.0.1:5499/iap/
// 샘플은 개발 환경으로 떠서 Unity 에디터 가짜 스토어 주문을 받는다. 실제 스토어는 설정이 있을 때만 붙인다:
//   --Iap:GooglePlay:PackageName=com.company.game --Iap:GooglePlay:ServiceAccountKeyFile=/secure/key.json
//   --Iap:AppStore:BundleId=com.company.game --Iap:AppStore:AppAppleId=1234567890
// 서버 알림: App Store 는 POST /iap/notifications/app-store, Google 은 Pub/Sub 푸시 설정이 있을 때 POST /iap/notifications/google-play
//   --Iap:GooglePlay:NotificationAudience=https://.../iap/notifications/google-play --Iap:GooglePlay:NotificationServiceAccountEmail=push@project.iam.gserviceaccount.com
// 계정은 X-Sample-Account 헤더로 받는다(샘플 전용). 운영에서는 account 패키지의 IAccessTokenValidator 로 Bearer 토큰을 검증한다(README).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = Environments.Development });
var settings = builder.Configuration.GetSection("Iap");
builder.Services.AddSingleton<SampleBalances>();

var iap = builder.Services.AddGeunedaIap(options =>
	{
		options.Products.Add(new IapServerProduct("gem_100", IapProductType.Consumable));
		options.Products.Add(new IapServerProduct("no_ads", IapProductType.NonConsumable));
		options.Products.Add(new IapServerProduct("vip_month", IapProductType.Subscription));
	})
	.AddFakeStore()
	.AddGrantor<SampleWallet>(ServiceLifetime.Singleton)
	.AddNotificationHandler<SampleNotifications>(ServiceLifetime.Singleton)
	.AddInMemoryLedger();

if (!string.IsNullOrEmpty(settings["GooglePlay:PackageName"]))
{
	iap.AddGooglePlay(options =>
	{
		options.PackageName = settings["GooglePlay:PackageName"];
		options.ServiceAccountKeyFile = settings["GooglePlay:ServiceAccountKeyFile"];
		options.NotificationAudience = settings["GooglePlay:NotificationAudience"];
		options.NotificationServiceAccountEmail = settings["GooglePlay:NotificationServiceAccountEmail"];
	});
}

if (!string.IsNullOrEmpty(settings["AppStore:BundleId"]))
{
	iap.AddAppStore(options =>
	{
		options.BundleId = settings["AppStore:BundleId"];
		options.AppAppleId = settings.GetValue<long?>("AppStore:AppAppleId");
	});
}

var app = builder.Build();
app.MapGeunedaIap("/iap", context =>
{
	var account = context.Request.Headers["X-Sample-Account"].ToString();
	return Task.FromResult(string.IsNullOrEmpty(account) ? null : account);
});
if (!string.IsNullOrEmpty(settings["GooglePlay:NotificationAudience"]))
{
	app.MapGeunedaIapGooglePlayNotifications("/iap");
}

if (!string.IsNullOrEmpty(settings["AppStore:BundleId"]))
{
	app.MapGeunedaIapAppStoreNotifications("/iap");
}

app.Run();
