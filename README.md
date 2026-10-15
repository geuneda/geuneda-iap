# Geuneda IAP

인앱 결제 패키지입니다. 결제한 주문은 게임의 지급자(서버 지급)가 지급을 확인한 뒤에만 스토어 거래를 닫습니다. 그래서 결제했는데 지급되지 않은 주문은 스토어에 남아 다시 처리됩니다. 새 구매, 이전 실행에서 남은 결제, 결제창 시간 초과 뒤에 늦게 온 결제, 승인 대기 뒤 승인된 결제, 중복 거래 모두 같은 지급 경로를 지납니다.

| 기능 | 타입 |
|------|------|
| 서비스 | `IIapService`, `IapService`, `IapServiceOptions` |
| 상품 | `IapProductDefinition`, `IapProductType`, `IapProduct` |
| 지급 계약(게임이 구현) | `IPurchaseFulfiller`, `FulfillmentResult`, `FulfillmentStatus` |
| 주문과 결과 | `IapOrder`, `IapOrderOrigin`, `PurchaseSettlement`, `IapOrderFailure`, `RecoverySummary`, `IapEntitlementStatus` |
| 결과 코드 | `IapResultCodes`(7000 블록) |
| 스토어 어댑터 계약 | `IIapStore`, `IapStoreOrder`, `IapStoreOrders`, `IapStorePurchaseFailure`, `IapStoreFailureReason`, `IapConfirmResult` |
| 매출 기록(analytics 가 있을 때) | `Geuneda.Iap.Analytics.IapRevenueLogger`, `IapPaidAmount` |
| 결제 서버 와이어 형식 | `IapRedeemRequest`, `IapRedeemResponse<TGrant>`, `IIapRedeemResponse`, `IapRedeemStatus`, `IapRedeemCodes`, `IapStoreNames`, `IapRoutes`, `IapRedeemResults` |
| 결제 서버 HTTP 지급자(http 가 있을 때) | `Geuneda.Iap.Http.HttpPurchaseFulfiller<TGrant>` |
| 결제 서버(.NET, `Server~`) | `Geuneda.Iap.Server`: 영수증 검증(Google Play, App Store), 영수증 유일성 원장, 지급 엔드포인트, 서버 알림(환불 회수, 구독 상태) |

네임스페이스는 `Geuneda.Iap` 입니다.

## 설치

```json
{
  "dependencies": {
    "com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask",
    "com.geuneda.core": "https://github.com/geuneda/geuneda-core.git",
    "com.geuneda.iap": "https://github.com/geuneda/geuneda-iap.git",
    "com.geuneda.iap.unity": "https://github.com/geuneda/geuneda-iap-unity.git"
  }
}
```

필수 의존성은 core 뿐입니다. 스토어 SDK 는 어댑터 패키지로 붙입니다(결정 D3).

| 패키지 | 내용 |
|---|---|
| `com.geuneda.iap.unity` | Unity IAP 5.4.4(`StoreController`) 어댑터 `UnityIapStore`. Unity 2022.3 이상 |

`com.geuneda.analytics` 0.2.0 이상이 있으면 `Geuneda.Iap.Analytics` 어셈블리가, `com.geuneda.http` 0.1.0 이상이 있으면 `Geuneda.Iap.Http` 어셈블리(결제 서버 HTTP 지급자)가 켜집니다(asmdef `versionDefines`).

## 사용 예

지급자는 영수증을 게임 서버에 보내고 서버의 답을 결과로 바꿉니다.

```csharp
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Iap;

public sealed class ShopFulfiller : IPurchaseFulfiller
{
	private readonly IShopApi _api;

	public ShopFulfiller(IShopApi api)
	{
		_api = api;
	}

	public async UniTask<FulfillmentResult> FulfillAsync(IapOrder order, CancellationToken cancellationToken)
	{
		if (!_api.IsSignedIn)
		{
			// 로그인 뒤 RecoverAsync 로 다시 지급합니다
			return FulfillmentResult.RetryLater("not signed in");
		}

		var response = await _api.BuyAsync(new BuyRequest
		{
			ProductId = order.ProductId,
			Store = order.StoreName,
			TransactionId = order.TransactionId,
			Receipt = order.Receipt,
			// App Store 는 Receipt 가 앱 영수증이라 비어 있을 수 있어, 거래 서명(JWS)을 함께 보냅니다
			SignedTransaction = order.SignedTransaction,
			Payload = order.Payload
		}, cancellationToken);

		switch (response.Status)
		{
			case BuyStatus.Granted:
				// 서버 응답 전체를 넘깁니다(보상 목록, 서버가 확인한 실결제 금액·통화)
				return FulfillmentResult.Granted(response);
			case BuyStatus.Duplicate:
				// 이미 지급한 영수증. 오류가 아니라 거래를 닫을 대상입니다
				return FulfillmentResult.AlreadyGranted(response);
			case BuyStatus.InvalidReceipt:
				return FulfillmentResult.Rejected(response.Message);
			default:
				return FulfillmentResult.RetryLater(response.Message);
		}
	}
}
```

결제 서버(`Server~`, 아래 [결제 서버](#결제-서버server))를 쓰면 지급자를 만들지 않고 HTTP 지급자를 씁니다. 응답(`IapRedeemResponse<TGrant>`)에 서버 지급자의 결과와 스토어가 확인한 실결제 금액이 옵니다.

```csharp
using Geuneda.Http;
using Geuneda.Iap.Http;

// 기본 주소는 서버 접두사까지(MapGeunedaIap("/iap") 면 .../iap/). access 토큰은 account 의 AuthTokenMiddleware 가 붙입니다
var http = new HttpClientBuilder()
	.WithBaseAddress("https://game.example.com/iap/")
	.AddMiddleware(new AuthTokenMiddleware(auth))
	.Build();
var fulfiller = new HttpPurchaseFulfiller<ShopGrant>(http);   // ShopGrant: 서버 지급자가 돌려주는 보상 형식

// 지급 결과: ((IapRedeemResponse<ShopGrant>) settlement.Fulfillment.Response).Grant
```

매출(`IapRevenueLogger`)의 기본 규칙은 응답이 결제 서버의 것(`IIapRedeemResponse`)이면 테스트 결제와 환불된 거래(`Revoked`)를 빼고 서버가 확인한 금액·통화를 씁니다(없으면 아래 표시 가격 규칙). `paidAmount` 를 넘기지 않아도 됩니다.

실시간 RPC 처럼 다른 경로로 서버를 부르면 `order.ToRedeemRequest()` 로 요청을 만들고 서버 응답을 `response.ToFulfillmentResult()` 로 바꿉니다(응답이 없거나 상태를 모르면 거래를 열어 두는 `RetryLater`).

```csharp
using Geuneda.Core;
using Geuneda.Iap;
using Geuneda.Iap.Unity;

var store = new UnityIapStore();
var iap = new IapService(store, new ShopFulfiller(api), new IapServiceOptions
{
	Products = new[]
	{
		new IapProductDefinition("gem_100", IapProductType.Consumable),
		new IapProductDefinition("no_ads", IapProductType.NonConsumable),
		new IapProductDefinition("vip_month", IapProductType.Subscription, "com.game.vip.month")
	}
});

// 지급 화면 밖에서 끝난 지급(이전 실행의 결제, 늦게 온 결제)
iap.OrderSettled += settlement =>
{
	if (!settlement.IsAwaited && settlement.Fulfillment.Status == FulfillmentStatus.Granted)
	{
		ShowRewardPopup(((BuyResponse) settlement.Fulfillment.Response).Rewards);
	}
};

// 지급 누락 추적(원격 로그로)
iap.OrderFailed += failure => remoteLog.Warn("iap_order_failed", failure.ToString());

// 결제창 동안 실시간 연결의 핑 시간 초과를 멈춤
iap.StoreUiChanged += open => realtime.SuspendPingTimeout(open);

var initialized = await iap.InitializeAsync();
```

```csharp
var result = await iap.PurchaseAsync("gem_100", payload: selectedRewardId);

if (result.IsSuccess)
{
	ShowRewardPopup(((BuyResponse) result.Value.Fulfillment.Response).Rewards);
}
else
{
	switch (result.Code)
	{
		case IapResultCodes.UserCancelled:
			break;
		case ResultCodes.Timeout:
		case ResultCodes.Cancelled:
		case IapResultCodes.Deferred:
		case IapResultCodes.DuplicateTransaction:
		case IapResultCodes.FulfillmentPending:
			// 결제가 됐을 수 있습니다. '결제 실패'라고 하지 말고 '처리 중'으로 안내합니다. 끝나면 OrderSettled 가 옵니다
			ShowToast("purchase_processing");
			break;
		default:
			ShowToast("purchase_failed");
			break;
	}
}
```

로그인한 직후와 실시간 연결이 다시 붙은 직후에는 `RecoverAsync()` 를 부릅니다. 복원 버튼은 `RestoreAsync()` 입니다(돌려받은 보유 주문을 서버에 보내 권한을 다시 줍니다). 앱 시작과 포그라운드 복귀 때는 서비스가 스스로 다시 조회합니다.

매출은 거래마다 한 번인 매출 지점(`PurchaseConfirmed`)에서만 기록합니다. 근거는 하나만 씁니다. 지급 결과가 결제 서버 응답이면 서버 원장의 첫 지급(`Granted`, 거래당 한 번)을 받은 기기가 거래를 닫을 때이고(서버가 Google 결제를 먼저 확인해 스토어가 '이미 닫힘'이라고 해도), 그 밖의 지급자는 스토어의 첫 확정(`Confirmed`)입니다. 둘을 섞으면 여러 기기가 같은 거래를 처리할 때 두 번 잡힙니다. 첫 지급 응답이 유실되면(서버는 지급했지만 클라이언트가 응답을 못 받고 다시 보내 `Duplicate` 를 받음) 클라이언트 매출 이벤트는 빠지므로, 정확한 매출 집계는 서버 원장(`PurchaseRecord` 의 `PaidAmount`, `IsTest`, `RedeemedAt`)으로 하세요. 금액은 실결제 총액이어야 하므로, 서버가 영수증 검증으로 확인한 금액과 거래 통화를 지급자 응답에 담아 `IapPaidAmount` 로 돌려주세요(이전 실행의 결제를 복구하면 상품 조회 통화가 결제 때와 다를 수 있음). 생략하면 구독은 기록하지 않고(무료 체험·첫 달 할인) 그 밖은 상품 조회 때의 표시 가격과 통화를 씁니다. 결제 서버 응답이면 서버가 확인한 수량으로 단가 × 수량을 기록합니다.

```csharp
using Geuneda.Iap.Analytics;

var revenue = new IapRevenueLogger(iap, analytics, paidAmount: settlement =>
{
	var response = (BuyResponse) settlement.Fulfillment.Response;
	return new IapPaidAmount(response.PaidAmount, response.PaidCurrency);
});
```

서비스와 어댑터는 `IIapService` 가 `IDisposable` 이라 `MainInstaller.CleanDispose<IIapService>()` 로 정리하고, 어댑터는 그 뒤에 `Dispose()` 합니다.

## 지급 누락을 막는 규칙

서비스가 지키는 것:

- **지급 확인 전에는 확정하지 않습니다.** 지급자가 `Granted`·`AlreadyGranted` 를 돌려줬을 때만 스토어 거래를 닫습니다.
- **실패한 지급은 거래를 열어 두고 다시 시도합니다.** `RetryLater`, 지급자 예외, 시간 초과(`FulfillTimeout`)는 5초부터 두 배씩 5분까지 백오프로 다시 지급합니다. 시간 초과 뒤에 온 결과는 믿지 않고 다시 묻습니다.
- **스토어를 다시 조회합니다.** 초기화, 포그라운드 복귀, `RecoverAsync`, 중복 거래 때마다 스토어에 남은 거래를 조회합니다(캐시만 보지 않음). 조회 실패는 빈 목록이 아니라 실패 코드입니다.
- **늦게 온 결제도 지급합니다.** 결제창이 제한 시간 안에 답하지 않거나 호출자가 기다리기를 그만둬도, 나중에 온 결제는 그 구매의 페이로드와 함께 뒤에서 지급합니다. 이때 스토어를 다시 조회해, 결제는 됐는데 이벤트만 놓친 경우도 찾습니다. 같은 상품을 다시 사면 먼저 다시 조회해 그 늦은 결제가 와 있으면 그것을 지급하고(그 구매의 페이로드로), 없을 때만 이전 페이로드를 버리고 결제창을 엽니다(늦은 결제가 어느 구매의 것인지 섞이지 않게).
- **결제창이 열린 동안 조회가 먼저 찾은 같은 상품의 새 주문은 보류합니다.** 결제창의 답이 오면 그 구매의 것으로 지급하고, 답 없이 끝나면 보류한 주문이 하나일 때 그 구매의 것으로, 그 밖은 남은 결제로 지급합니다(구매 결과와 페이로드를 잃지 않게).
- **같은 상품의 미확정 주문을 먼저 지급합니다.** 구매를 누르면 결제창을 열기 전에 같은 상품의 남은 주문을 지급합니다. 스토어가 중복 거래로 거절하면 다시 조회해 그 주문을 지급합니다.
- **조회가 실패하면 백오프로 다시 조회합니다**(앱이 켜져 있는 동안). 중복 거래 뒤 조회가 실패해도 뒤에서 계속 복구합니다.
- **확정 전에 스토어를 다시 연결합니다.** 확정 재시도는 백오프로만 깨므로, 끊긴 스토어를 서비스가 연결합니다.
- **이벤트는 상태를 다 바꾼 뒤에 올립니다.** 처리기가 복구·종료를 불러도 진행 중인 구매가 결과를 잃지 않고, 결제창 열림 처리기에서 종료하면 결제를 시작하지 않습니다. 결제창 닫힘 알림은 구매 결과보다 먼저 와서, 결과를 받자마자 연 새 결제창의 상태를 덮지 않습니다.
- **확정만 실패하면 확정만 다시 합니다.** 지급은 끝났으므로 구매는 성공(`IsConfirmed` false)이고 지급자를 다시 부르지 않습니다.
- **주문 하나씩, 거래 ID 로 한 번만.** 같은 거래가 두 번 와도 지급자는 동시에 한 번만 불립니다. 이번 실행에서 닫은 거래가 다시 오면 무시합니다.
- **종류를 모르는 주문은 확정하지 않습니다.** 스토어나 정의가 모르는 상품의 주문은 지급도 확정도 하지 않고 `OrderFailed`(`UnknownProduct`)로 알립니다. Google 은 종류를 모르는 주문을 확정하면 소비 대신 승인만 해서 영구 보유 상태가 됩니다. 판매를 그만둔 상품도 정의에서 빼지 마세요. 이미 받은 거래가 종류를 모르거나 다른 상품으로 다시 와도 검증한 원본을 덮어쓰지 않습니다. 같은 거래가 새 영수증으로 다시 오면 다음 지급 시도는 새 영수증을 씁니다.
- **구매·복원은 한 번에 하나입니다**(`InProgress`).

서버가 지켜야 하는 것(결제 서버 헬퍼가 지킴):

- **영수증(또는 거래 ID)을 유일 키로 저장하고 지급과 같은 트랜잭션에서 처리합니다.** 검증만 하고 지급을 클라이언트가 따로 저장하면, 그 사이에 앱이 죽었을 때 재시도가 '이미 쓴 영수증'으로 거절되어 지급이 영구히 누락됩니다.
- **같은 영수증이 다시 오면 `AlreadyGranted` 로 답합니다**(오류 아님). 지급자는 시간 초과 뒤 같은 주문을 다시 보낼 수 있습니다.
- **App Store 는 `SignedTransaction`(JWS)으로 검증합니다.** `Receipt` 의 앱 영수증은 StoreKit 2 에서 비어 있을 수 있습니다.

## 결과 코드

| 코드 | 뜻 | 결제됐을 수 있음 |
|---|---|---|
| `ResultCodes.Cancelled`(2) | 호출자가 기다리기를 그만둠 | 예 |
| `ResultCodes.Timeout`(3) | 스토어가 `StoreResponseTimeout` 안에 답하지 않음 | 예 |
| `ResultCodes.InProgress`(5) | 다른 구매·복원이 진행 중 | 아니오 |
| 7001 `NotInitialized` | 초기화 전 | 아니오 |
| 7002 `StoreUnavailable` | 스토어 연결·결제 기능 불가 | 아니오 |
| 7003 `ProductNotFound` | 스토어에서 받지 못한 상품 | 아니오 |
| 7004 `ProductUnavailable` | 지금 팔 수 없는 상품 | 아니오 |
| 7005 `UserCancelled` | 사용자가 결제창을 닫음 | 아니오 |
| 7006 `Deferred` | 승인 대기(자녀 보호, 현장 결제) | 승인되면 |
| 7007 `PaymentDeclined` | 결제 수단 거절 | 아니오 |
| 7008 `DuplicateTransaction` | 끝나지 않은 이전 거래가 있음 | 이전 결제 |
| 7009 `FulfillmentPending` | 결제됐지만 지급을 다시 시도 중 | 예 |
| 7010 `FulfillmentRejected` | 지급자가 영수증을 거절 | 예(지급 안 됨) |
| 7011 `StoreValidationFailed` | 스토어 쪽 서명·검증 실패 | 아니오 |
| 7012 `ExistingPurchasePending` | 스토어에 진행 중인 구매가 있거나, 이전 결제의 확정이 아직 안 됨 | 이전 결제 |
| 7013 `StoreError` | 분류할 수 없는 스토어 오류, 어댑터 예외 | 알 수 없음 |
| 7014 `NoProducts` | 초기화 때 상품을 하나도 받지 못함 | 아니오 |
| 7015 `UnknownProduct` | 종류를 모르는 주문(`OrderFailed` 로만) | 예 |
| 7016 `ConfirmFailed` | 지급 뒤 확정만 실패(`OrderFailed` 로만) | 예(지급됨) |

## 결제 서버(`Server~`)

ASP.NET Core(.NET 10) 라이브러리 `Geuneda.Iap.Server` 입니다. 지급 요청을 받아 스토어에 검증하고, 영수증 유일성 원장과 같은 트랜잭션에서 게임 지급자를 부릅니다. 와이어 형식(`Runtime/Protocol`)과 상품 종류는 Unity 패키지 파일을 링크해 함께 씁니다.

```
Server~/
  Geuneda.Iap.Server.slnx
  src/Geuneda.Iap.Server/          라이브러리
  tests/Geuneda.Iap.Server.Tests/  xUnit
  samples/Geuneda.Iap.SampleServer/ 가짜 스토어 + 메모리 원장 샘플
```

```csharp
using Geuneda.Iap;
using Geuneda.Iap.Server;

builder.Services.AddGeunedaIap(options =>
	{
		options.Products.Add(new IapServerProduct("gem_100", IapProductType.Consumable));
		options.Products.Add(new IapServerProduct("no_ads", IapProductType.NonConsumable));
		options.Products.Add(new IapServerProduct("vip_month", IapProductType.Subscription, googlePlayId: "vip_month", appStoreId: "com.game.vip.month"));
	})
	.AddGooglePlay(options =>
	{
		options.PackageName = "com.company.game";
		options.ServiceAccountKeyJson = secrets.GooglePlayKey;   // 없으면 키 파일, 그것도 없으면 Application Default Credentials
	})
	.AddAppStore(options => options.BundleId = "com.company.game")
	.AddGrantor<ShopGrantor>()     // 게임 지급(요청 범위)
	.AddLedger<SqlPurchaseLedger>(); // 게임 DB 원장(요청 범위). 개발은 AddInMemoryLedger(), 에디터 가짜 스토어는 AddFakeStore()(Production 금지)

var app = builder.Build();

// account 의 access 토큰으로 계정을 확인합니다. null 이면 401
app.MapGeunedaIap("/iap", async context =>
{
	var header = context.Request.Headers.Authorization.ToString();
	var token = header.StartsWith("Bearer ") ? header.Substring(7) : null;
	var validation = await context.RequestServices.GetRequiredService<IAccessTokenValidator>().ValidateAsync(token, context.RequestAborted);
	return validation.IsValid ? validation.AccountId : null;
});
```

```csharp
public sealed class ShopGrantor : IPurchaseGrantor
{
	private readonly GameDb _db;   // 원장과 같은 요청 범위 DB 컨텍스트

	public ShopGrantor(GameDb db)
	{
		_db = db;
	}

	public async Task<GrantResult> GrantAsync(PurchaseGrant grant, CancellationToken cancellationToken)
	{
		// 원장 트랜잭션 안. 여기서 쓴 것은 원장 기록과 함께 커밋되거나 함께 롤백됩니다
		// grant.Quantity: 지급할 수량(지급 요청 전에 일부가 환불됐으면 그만큼 적음)
		var rewards = await _db.GiveAsync(grant.AccountId, grant.Product.Id, grant.Quantity, cancellationToken);

		// 구독 권한은 여기서 쓰지 않습니다. 서버 알림을 쓰면 서버가 이 트랜잭션 안에서 합친 구독 스냅숏을 처리기에 넘깁니다(아래 '구독 권한')
		return GrantResult.Granted(new ShopGrant { Rewards = rewards });
	}
}
```

처리 순서(`IPurchaseRedeemer`, HTTP 밖에서 직접 부를 수도 있음):

1. 요청 검사(스토어, 상품 목록, 거래 ID). 서버 상품 목록에 없는 상품은 거절합니다.
2. 원장 확인. 지급 기록이 있으면 스토어를 부르지 않고 답합니다. 같은 계정이면 처음 지급 응답과 함께 `Duplicate`, 다른 계정이면 지급 없이 `OwnedByOtherAccount`(둘 다 `AlreadyGranted` 라 클라이언트가 거래를 닫음). 계정 없는 지급 전 환불 기록(아래 서버 알림)뿐이면 검증으로 넘어갑니다.
3. 스토어 검증.
4. 원장 트랜잭션 안에서 지급자를 부르고, 지급했으면 지급 응답과 함께 기록해 커밋합니다. 같은 거래의 동시 요청은 원장이 하나만 통과시킵니다.
5. Google 은 지급을 기록한 뒤 결제를 확인(acknowledge)합니다(`AcknowledgeAfterGrant`, 기본 켜짐). 클라이언트가 확정하기 전에 앱이 지워져도 3일 자동 환불이 일어나지 않습니다. 소비는 하지 않습니다(서버가 소비하면 클라이언트 확정이 '구매 없음'으로 실패).
   확인할 결제(Google 이 '확인됨'이라고 분명히 답하지 않은 모든 결제)는 지급과 같은 트랜잭션에서 원장에 다음 시도 시각(`PurchaseRecord.AcknowledgementDueAt`)으로 남습니다. 커밋 직후 확인이 실패하거나 그 사이 서버가 죽어도, 확인 작업자(호스트 서비스)가 시작할 때와 `AcknowledgementRetryInterval`(기본 5분)마다 시각이 된 기록을 모두 다시 확인합니다(실패한 기록은 다음 간격 뒤로 미뤄 뒤의 기록을 막지 않음). 확인이 거절되면 결제 상태를 다시 읽어 이미 확인·소비·취소됐으면 끝내고, 결제 뒤 3일이 지나면 Error 로그를 남기고 그만둡니다.

원장(`IPurchaseLedger`)은 게임 DB 로 구현합니다. (Store, TransactionId) 에 유일 제약을 둔 거래 표와 (Store, SubscriptionId) 키의 구독 표를 둡니다. 지급은 한 트랜잭션에서 (1) 지급 기록이 있으면 `Duplicate`, (2) 없으면 지급 콜백(지급자의 쓰기도 이 트랜잭션 안, 인자는 지급 전 환불 기록이 있으면 그것), (3) 지급했으면 기록 추가(지급 전 환불 기록이 있었으면 그 행을 지급 기록으로 바꾸고 환불 수량·시각은 유지) 후 커밋합니다. 동시 요청이 유일 제약에 걸리면 지급까지 함께 롤백하고 처음부터 다시 합니다. 확인 대기 기록 조회(`FindDueAcknowledgementsAsync(now, limit)`, 시각 순), 표시 해제(`MarkAcknowledgedAsync`), 미루기(`DeferAcknowledgementAsync`, 이미 해제된 기록은 되돌리지 않음)도 구현합니다(`AcknowledgementDueAt` 에 색인). 서버 알림을 쓰면 환불 수량 변경(`UpdateRefundAsync(RefundChange)`: 기록이 없으면 계정 없는 지급 전 환불 기록을 넣고 `RecordedBeforeGrant`, `EventOrdered` 면 이벤트가 마지막 변경보다 이를 때 `Stale`·수량이 같으면 시각만 앞당기고 `Unchanged`, `NeverDecrease` 면 기록과 새 값 중 큰 값, 지급 기록의 수량이 바뀌면 같은 트랜잭션에서 콜백 후 기록), 원래 거래 ID 로 찾기(`FindByOriginalTransactionAsync`, 지급 기록만, `OriginalTransactionId` 에 색인), 구독 스냅숏(`UpdateSubscriptionAsync`: 구독 행을 잠근 채 저장된 스냅숏으로 콜백을 부르고, 돌려받은 것이 null 이면 아무것도 하지 않고, 아니면 처리기 호출 후 저장. 순서·합치기는 콜백이 정하므로 원장은 비교하지 않음. 구독 지급은 지급 콜백 안에서 부르므로 지급 트랜잭션에 참여하고, 잠금 순서는 언제나 거래 행 → 구독 행. 지급 콜백 안에서 시작한 원장 작업은 콜백이 돌아가기 전에 모두 기다려야 함)도 구현합니다. 기존 행을 고치는 이 메서드들은 유일 제약만으로는 동시 처리를 막지 못합니다. 트랜잭션 안에서 그 행을 잠그고(없으면 넣고 잠금) 다시 읽어 비교·콜백·저장하거나, 버전 열 조건부 갱신이 실패하면 게임 쓰기까지 전부 롤백하고 다시 하세요. 구독 잠금은 여러 서버에 걸쳐야 하므로 DB 에 둡니다(Google 은 그 안에서 조회). 확인 표시와 환불 갱신은 서로의 열을 덮어쓰지 않아야 합니다. 지급자와 원장이 같은 요청 범위 DB 컨텍스트·세션을 받게 등록합니다. App Store 와 Mac App Store 는 거래 ID 공간이 같아 원장 스토어는 `AppleAppStore` 하나입니다(`IStoreVerifier.LedgerStore`).

검증 규칙:

| 스토어 | 방법 | 지급 | 거래만 닫음(`AlreadyGranted` `Revoked`) | 다시 시도(`RetryLater`) | 거절(`Rejected`) |
|---|---|---|---|---|---|
| Google Play | 구매 토큰으로 Android Publisher API 조회(일회성 `productsv2`, 구독 `subscriptionsv2`). 서비스 계정에 '주문 및 구독 관리' 권한 | PURCHASED, 구독은 결제 대기·결제 전 취소 밖의 모든 상태(만료 포함, 상태와 만료 시각을 지급자에 넘김) | CANCELLED, 결제 전에 취소된 구독 | PENDING, 네트워크·시간 초과·429·5xx, 401·403(서비스 계정 권한, Error 로그), 모르는 상태, 상품 목록이 없거나 상품 ID 가 빈 응답 | 400·404·410(모르는 토큰), 상품 목록에 요청 상품이 없음 |
| App Store·Mac App Store | `SignedTransaction`(StoreKit 2 JWS)을 Apple 공식 라이브러리 `SignedDataVerifier` 와 같은 규칙으로 검증: ES256, x5c 3개, 인증서 OID, 내장 Apple Root CA - G3 까지 체인, 온라인 검사(지금 시각, OCSP). 체인 신뢰는 인증서 내려받기 없이 먼저 확인하고 신뢰한 체인에만 OCSP 를 묻습니다(공격자 인증서의 주소로 서버가 요청하지 않게) | Production, Sandbox(`AllowSandbox`, 기본 켜짐, App Store 심사가 운영 서버에 샌드박스 결제를 보냄) | `revocationDate` 있음(환불·철회) | OCSP 에 닿지 못함 | 서명·체인 오류, 다른 번들 ID, 거래 ID·상품 불일치, Xcode·LocalTesting |
| 가짜 스토어(`fake`) | 검증 없음(개발 서버 전용) | 항상 | | | |

테스트 결제(Google 라이선스 테스터, 샌드박스, 가짜 스토어)는 응답의 `IsTest` 가 true 입니다. `AllowTestPurchases` 를 끄면 거절합니다(심사 구매도 실패하니 주의). 실결제 금액은 App Store 는 거래 서명의 `price`·`currency`, Google 은 `ReadOrderTotal`(주문 API, '재무 데이터 보기' 권한, 실패해도 지급)을 켰을 때만 옵니다.

엔드포인트 `POST {prefix}/purchases` 의 응답:

| HTTP | 경우 |
|---|---|
| 200 | 그 밖의 모든 결과(본문의 `status` 로 구분) |
| 400 | 형식 오류, 필수 필드 없음(`BadRequest`) |
| 401 | 계정 확인 실패(`Unauthorized`, 클라이언트는 다시 시도) |
| 413, 415 | 본문 초과(`MaxRequestBytes`, 기본 1MB), JSON 아님 |
| 500 | 지급자·원장 예외. 지급 트랜잭션 안이면 롤백(기록 안 됨), 커밋 뒤 확인 표시 중이면 이미 지급됨. 어느 쪽이든 클라이언트가 같은 거래로 다시 보내 지급받거나 `Duplicate` 를 받음 |

이유 코드(`IapRedeemCodes`, 7100~7199):

| 코드 | 상태 | 뜻 |
|---|---|---|
| 0 `None` | Granted | 이번에 지급 |
| 7101 `Duplicate` | AlreadyGranted | 이 계정에 이미 지급(처음 지급 응답을 다시 줌) |
| 7102 `OwnedByOtherAccount` | AlreadyGranted | 다른 계정에 지급한 거래(지급 없음) |
| 7103 `Revoked` | AlreadyGranted | 환불·취소된 거래(지급 없음) |
| 7104 `InvalidReceipt` | Rejected | 서명·형식 오류, 스토어가 모르는 거래 |
| 7105 `AppMismatch` | Rejected | 다른 앱의 거래 |
| 7106 `ProductMismatch` | Rejected | 서버 상품 목록에 없거나 거래 상품이 다름 |
| 7107 `UnsupportedStore` | Rejected | 서버에 검증기가 없는 스토어 |
| 7108 `EnvironmentNotAllowed` | Rejected | 허용하지 않은 환경(샌드박스, 테스트 결제, Xcode) |
| 7109 `PaymentPending` | RetryLater | 결제 대기 |
| 7110 `StoreUnavailable` | RetryLater | 스토어 API 를 쓸 수 없음 |
| 7111 `GrantRejected` | Rejected | 게임 지급자가 거절 |
| 7112 `GrantDeferred` | RetryLater | 게임 지급자가 나중에 |
| 7113 `BadRequest` | Rejected | 요청 형식 오류(HTTP 400) |
| 7114 `Unauthorized` | RetryLater | 계정 확인 실패(HTTP 401) |

### 서버 알림(환불·구독)

Google Play 실시간 개발자 알림(RTDN)과 App Store Server Notifications V2 를 받아 환불을 회수하고 구독 상태를 게임에 알립니다. 게임은 `IPurchaseNotificationHandler` 를 구현합니다.

```csharp
builder.Services.AddGeunedaIap(...)
	.AddGooglePlay(options =>
	{
		options.PackageName = "com.company.game";
		options.NotificationAudience = "https://game.example.com/iap/notifications/google-play";   // 푸시 구독의 인증 대상
		options.NotificationServiceAccountEmail = "rtdn-push@project.iam.gserviceaccount.com";      // 푸시 구독의 서비스 계정
	})
	.AddAppStore(options =>
	{
		options.BundleId = "com.company.game";
		options.AppAppleId = 1234567890;   // App Store Connect 의 Apple ID. 운영 알림의 appAppleId 를 확인
	})
	.AddNotificationHandler<ShopNotifications>()   // 원장과 같은 요청 범위
	.AddLedger<SqlPurchaseLedger>();

app.MapGeunedaIapGooglePlayNotifications("/iap");   // POST /iap/notifications/google-play (Pub/Sub 푸시 엔드포인트, 인증 사용)
app.MapGeunedaIapAppStoreNotifications("/iap");     // POST /iap/notifications/app-store (App Store Connect 의 서버 알림 URL, 버전 2)
```

```csharp
public sealed class ShopNotifications : IPurchaseNotificationHandler
{
	// 원장 트랜잭션 안. 회수한 것은 원장의 환불 수량과 함께 커밋되거나 함께 롤백됩니다
	public Task OnRefundedAsync(PurchaseRefund refund, CancellationToken ct) => _db.RevokeAsync(refund.AccountId, refund.Product.Id, refund.Quantity, ct);

	// App Store 가 분쟁 뒤 환불을 취소함
	public Task OnRefundReversedAsync(PurchaseRefund refund, CancellationToken ct) => _db.GiveAsync(refund.AccountId, refund.Product.Id, refund.Quantity, ct);

	// 구독 권한(아래 '구독 권한'). 서버가 합친 스냅숏을 구독 ID 로 그대로 저장. 구독 지급 때는 지급 트랜잭션 안에서 옴
	public Task OnSubscriptionChangedAsync(SubscriptionUpdate update, CancellationToken ct) => _db.SaveSubscriptionAsync(update, ct);
}
```

- **환불은 누적 수량으로 한 번만 반영합니다.** 원장의 `PurchaseRecord.RefundedQuantity` 를 스토어가 알려 준 누적 환불 수량으로 맞추고(`IPurchaseLedger.UpdateRefundAsync`, 한 트랜잭션), 늘어난 만큼 `OnRefundedAsync`, 줄어든 만큼 `OnRefundReversedAsync` 를 부릅니다. 같은 알림이 다시 오면 수량이 같아 부르지 않습니다. 순서는 스토어마다 다르게 정합니다. App Store 는 서명된 이벤트라 이벤트 시각 순(`EventOrdered`: 마지막 반영보다 이른 이벤트는 무시, 수량이 같아도 늦은 이벤트면 시각을 앞당겨 먼저 온 환불 취소 뒤의 늦은 환불을 거름)이고, Google 은 서버가 조회한 지금 상태라 시각으로 거르지 않고 줄이지 않습니다(`NeverDecrease`, Google 은 환불을 취소하지 않음). 처리기가 예외를 던지면 롤백하고 500 으로 답해 스토어가 다시 보냅니다. 모두 환불된 거래가 클라이언트에서 다시 오면 지급 없이 닫습니다(`Revoked`).
- **지급 요청보다 먼저 온 환불도 남깁니다.** 원장에 없는 거래의 환불은 계정 없는 지급 전 환불 기록(`PurchaseRecord.IsGranted` false)으로 남깁니다. 나중에 그 거래의 지급 요청이 오면 같은 트랜잭션에서 그 기록을 보고 환불된 수량만큼 빼고 지급하며(`PurchaseGrant.Quantity`), 전부 환불이면 지급 없이, 계정도 기록하지 않고 닫습니다(`Revoked`, '한계' 참고). 그래서 구매 뒤 환불받고 환불 전에 받은 영수증(여전히 서명이 유효한 JWS)을 나중에 내도 지급되지 않습니다. 진행 중인 지급과 겹친 환불은 같은 거래 잠금에서 지급 커밋을 기다렸다가 회수합니다.
- **모르는 값은 회수하지 않고 다시 받습니다.** 모르는 환불 종류·상품 종류, 구독 상태가 없거나 모르는 값, 구매 시각(`purchaseDate`, 결제 기간의 기준)이 없는 App Store 구독 거래, 상품 목록이 없는 Google 응답은 503 입니다(결제 대기·결제 전 취소 구독은 알릴 것이 없어 200).
- **Google:** 일회성 환불(`voidedPurchaseNotification`)의 전체 환불은 지급 수량 전부, 여러 개 구매의 일부 환불(여러 번 올 수 있음)은 지금 남은 환불 가능 수량(`refundableQuantity`)으로 누적 환불 수량을 구합니다(조회 실패는 503 으로 다시 받음). 구독 알림은 원장의 구독 잠금 안에서 알림 내용 대신 `subscriptionsv2` 로 지금 상태를 다시 읽어 넘깁니다(늦게 끝난 옛 조회가 새 상태를 덮지 못함, `ObservedAt` 은 저장된 것보다 늦은 조회 시각). `SUBSCRIPTION_REVOKED` 는 지급도 회수하고(지급 전이면 지급 전 환불 기록), 구독 결제의 환불(`voidedPurchaseNotification` 구독)은 상태만 넘깁니다(토큰 하나에 주문이 여럿이라 지급 전에는 처음 주문인지 알 수 없고, 환불 뒤에도 기간이 남을 수 있음. 철회됐으면 상태가 만료로 옴). 계정은 원장에서 찾고, 업그레이드·재가입한 새 구독이 아직 지급 요청 전이면 `linkedPurchaseToken` 의 계정을 씁니다.
- **App Store:** 알림·거래·갱신 정보 서명을 모두 검증하고 번들 ID, 환경(`AllowSandbox`), 운영 알림의 `AppAppleId` 를 확인합니다. `REFUND`·`REVOKE` 는 거래 전체를 환불로, `REFUND_REVERSED` 는 환불 없음으로 맞춥니다. 구독 상품이면 알림의 `status`(1 이용 중, 2 만료, 3 결제 재시도, 4 결제 유예, 5 철회)와 자동 갱신 상태로 `IapSubscriptionState` 를 정해 넘기고, 결제 유예 종료 시각(`GracePeriodExpiresAt`)도 넘깁니다. 계정은 구독 잠금 안에서 원래 거래 ID 로 지급한 가장 최근 기록에서 찾습니다(`FindByOriginalTransactionAsync`, 갱신 거래만 지급한 구독 포함).
- **구독 권한은 서버가 합쳐 넘깁니다.** 원장의 구독 행(`UpdateSubscriptionAsync`)에 합친 권한 스냅숏(`SubscriptionUpdate`)을 두고, 서버 알림과 구독 지급을 그 구독 잠금 안에서 합쳐 바뀔 때마다 처리기에 넘깁니다. 처리기는 받은 것을 `SubscriptionId` 로 그대로 저장하면 되고, 지급자는 구독 권한을 쓰지 않습니다. 모르는 App Store `status`(1~5 밖)는 상태를 바꾸지 않고 503 입니다.
  - 구독 지급: 지급 트랜잭션 안에서 구독 잠금을 잡고 합칩니다(그 계정이 소유자). 지급 검증의 조회는 잠금 밖이라 쓰지 않고, Google 은 잠금 안에서 다시 읽습니다. 읽지 못하면 지급까지 롤백하고 `RetryLater`(`StoreUnavailable`)로 답해 클라이언트가 다시 보내므로, 지급만 남고 권한이 빠지는 일이 없습니다.
  - Google: 잠금 안에서 지금 상태를 다시 읽은 것이 항상 이깁니다(알림 내용, 잠금 밖 지급 조회는 쓰지 않음).
  - App Store(가짜 스토어·사용자 정의 스토어도 지급한 거래로 같은 규칙): 거래·알림은 그 결제 기간의 것이라 기간(`PeriodStartedAt`, 거래의 구매 시각)이 뒤인 것이 이기고, 같은 기간이면 늦게 서명된 알림이 이깁니다. 만료 시각은 기준으로 쓰지 않습니다(긴 상품에서 짧은 상품으로 업그레이드하면 새 거래의 만료가 더 이름). 지급한 거래 서명에는 자동 갱신·유예 정보가 없어 뒤 기간일 때만 상태를 바꾸고(자동 갱신은 앞 스냅숏의 것), 그 밖에는 계정만 연결합니다. 그래서 갱신 거래를 지급하면 그 알림이 늦거나 오지 않아도 권한이 오르고, 늦게 오거나 다시 온 앞 기간 알림은 권한을 되돌리지 않으며 순서 기준도 바꾸지 않습니다.
  - 계정: 스냅숏이 정합니다. 소유자를 정한 지급의 기간(`AccountPeriodStartedAt`)보다 같거나 뒤 기간의 거래를 지급받은 계정이 소유자가 되고, 앞 기간 거래를 늦게 지급하면 바뀌지 않습니다. 알림은 상태 기간만 올리고 소유자를 바꾸지 않으므로, 같은 지급들이면 알림이 언제 오든 소유자가 같습니다. 알림은 스냅숏에 계정이 없을 때만 원장에서 찾습니다(Google 은 그 토큰, 없으면 연결 토큰의 지급 기록, App Store 는 원래 거래 ID 로 지급한 가장 최근 기록). 지급 요청 전에 온 갱신은 `AccountId` 가 null 이고, 그 지급이 오면 같은 트랜잭션에서 계정을 넣은 스냅숏(`REDEEM`)이 다시 옵니다. 계정이 바뀌면(다른 계정이 뒤 기간 거래를 지급받음) 권한을 그 계정으로 옮기세요.
  - 서버 알림을 쓰지 않으면(처리기 없음) 합치지 않습니다. 그때는 지급자가 `grant.Purchase` 의 상태·만료로 권한을 정하고, 갱신·환불은 반영되지 않습니다. 사용자 정의 검증기(`AddVerifier<T>`)는 구독 거래에 `VerifiedPurchase.SubscriptionState` 와 `PurchasedAt`(결제 기간의 시작, 기간 순서의 기준)을 채워야 합니다. 없으면 서버 알림을 쓸 때 지급자를 부르기 전에 예외(500)입니다.
- **알림으로 지급하거나 확인하지 않습니다.** 결제 알림(`ONE_TIME_PRODUCT_PURCHASED`, `SUBSCRIPTION_PURCHASED` 등)만으로는 지급할 계정을 모르고, 지급하지 않은 결제를 확인하면 클라이언트가 끝내 지급받지 못한 결제의 Google 자동 환불(마지막 안전망)을 막게 됩니다. 지급은 클라이언트의 지급 요청으로 합니다.
- **응답:** 200(반영·할 일 없음: 테스트 알림, 다른 앱·패키지, 이 서버가 지급하지 않은 거래), 503(스토어 API·인증서 폐기 확인 불가, 다시 보내 줌), 401(서명·토큰 검증 실패), 400(본문을 해석하지 못함 — Pub/Sub 이 보관 기간 동안 다시 보내므로 버그를 고친 뒤 받을 수 있음), 500(처리기·원장 예외).
- **Pub/Sub 푸시 인증:** OIDC 토큰의 Google 서명(JWKS), 발급자 `accounts.google.com`, 대상(`NotificationAudience`), 만료, 서비스 계정 이메일과 이메일 확인을 봅니다(Google.Apis.Auth `JsonWebSignature.VerifySignedTokenAsync`, 토큰 시각은 시스템 시계).

실행: `dotnet test "Server~/Geuneda.Iap.Server.slnx"`. 샘플 서버는 `dotnet run --project "Server~/samples/Geuneda.Iap.SampleServer" --no-launch-profile -- --urls http://127.0.0.1:5499` 로 띄우고, Unity 상호 운용 테스트(`SampleServerInteropTest`)는 `GEUNEDA_IAP_INTEROP_URL=http://127.0.0.1:5499/iap/` 로 이름을 골라 실행합니다.

## 스토어 어댑터 만들기

`IIapStore` 를 구현합니다. 서비스가 순서·중복·재시도·시간 제한을 맡으므로 어댑터는 SDK 호출과 형식 변환만 합니다.

- 이벤트는 메인 스레드에서 올립니다(다른 스레드면 서비스가 메인 스레드로 넘김).
- `FetchOrdersAsync` 는 조회 실패를 빈 목록으로 돌려주지 않습니다.
- `IapStoreOrder.IsKnownProduct` 는 스토어가 상품 종류를 알 때만 true 입니다.
- 실패 이유는 SDK 의 정수 값을 옮기지 말고 멤버마다 대응시킵니다.
- 확정 결과는 `Confirmed`, 이미 닫힌 거래면 `AlreadyConfirmed`, 그 밖은 `Failed` 입니다. 서비스는 확정을 기다리다 시간이 지나면 나중에 같은 거래로 다시 부릅니다. 그 사이 앞 요청이 성공했으면 다시 확정하지 말고 그 결과를 돌려줘야 합니다(Google 은 이미 소비된 구매를 다시 확정하면 찾지 못해 실패함).

## 한계

- `Payload` 는 메모리에만 있습니다. 앱을 다시 시작한 뒤 복구한 주문은 `Payload` 가 null 입니다. 지급에 꼭 필요한 선택은 결제 전에 서버에 저장하거나 복구 때 다시 물으세요. 승인 대기(`Deferred`) 결제가 같은 상품의 새 결제창과 겹쳐 승인되면 새 구매의 것으로 보일 수 있습니다.
- 이번 실행에서 거절(`Rejected`)한 주문은 `RecoverAsync`, 같은 상품 구매, 다음 실행 때만 다시 시도합니다. Google 은 3일 안에 확정하지 않은 결제를 자동 환불합니다.
- 구독 만료·갱신 판정은 서버가 합니다(서버 알림의 `OnSubscriptionChangedAsync`). `CheckEntitlementAsync` 는 기기의 스토어 계정 기준입니다.
- 서버 알림은 App Store `CONSUMPTION_REQUEST`(환불 심사용 사용 정보 요청)에 답하지 않고, Google 구독의 갱신 결제 환불은 지급 회수 없이 상태만 넘깁니다. 알림은 테스트 인증서·로컬 JWKS 로만 확인했고 실제 Pub/Sub·App Store 알림은 확인하지 않았습니다.
- 실제 Google Play·App Store 결제는 이 패키지 테스트에서 확인하지 않았습니다(어댑터는 Unity IAP 가짜 스토어로, 서버 검증은 가짜 Android Publisher API 와 Apple 과 같은 모양의 테스트 인증서로 확인). 실제 Apple 인증서의 OCSP 확인도 확인하지 않았습니다.
- 서버는 클라이언트가 구매 때 넣은 계정 표시(Google `obfuscatedAccountId`, App Store `appAccountToken`)를 지급자에 넘기기만 하고 확인하지 않습니다(`VerifiedPurchase.AccountToken`). 어댑터는 아직 이 값을 넣지 않습니다.
- 서버 알림의 503(다시 받음)은 알림을 성공으로 버리지 않는다는 뜻이지 복구 보장이 아닙니다. 같은 문제가 계속되면(예: `purchaseDate` 없는 App Store 구독 거래, 모르는 상태) 스토어가 재전송을 그만둔 뒤 이 패키지 안에는 영속 재처리·자동 재조회가 남지 않습니다. 해당 Error 로그를 운영 경보로 받으세요.
- 지급 요청 전에 모두 환불된 결제는 계정을 기록하지 않고 닫습니다(`Revoked`, 지급하지 않은 계정이 구독 소유자나 환불 취소 수취인이 되지 않게). 그 뒤 App Store 가 환불을 취소해도(`REFUND_REVERSED`) 자동으로 지급하지 못하고 Warning 로그(거래 ID, 상품)를 남깁니다. 지급 요청 때의 Warning 로그(계정, 거래)와 맞춰 고객 지원으로 지급하세요.
- 이전 서버가 이미 지급한 결제는 새 원장에 없으므로, 원장을 옮기지 않고 바꾸면 남은 거래가 다시 지급될 수 있습니다. `FindAsync` 가 이전 기록도 보게 구현하세요.

## 테스트

- `Geuneda.Iap.Tests.EditMode`: 가짜 스토어·지급자·수동 지연으로 구매, 지급 재시도와 백오프, 확정만 재시도, 시간 초과와 늦은 결제, 승인 대기, 중복 거래, 같은 상품 미확정 주문, 거래 중복 제거, 복구·재조회·복원, 종류 모르는 주문, 초기화 실패와 복귀 재시도, 종료, 다른 스레드에서 끝난 지급, 재진입(처리기의 복구·종료)과 조회·결제창 경합(`IapServiceRaceTest`).
- `Geuneda.Iap.Analytics.Tests.EditMode`: 확정 성공 한 번만 매출 기록(서버가 먼저 확인한 첫 지급 포함), 실결제 금액, 구독 기본 제외, 결제 서버 응답의 금액·테스트 결제·환불.
- `Geuneda.Iap.Http.Tests.EditMode`: 응답 → 지급 결과 변환(확실하지 않으면 다시 시도), 요청 본문, 샘플 서버 상호 운용(`SampleServerInteropTest`, 명시 실행).
- `Server~/tests/Geuneda.Iap.Server.Tests`: 원장(한 번만 지급, 동시 요청, 지급자 거절·예외 롤백, 확인 대기), 중복·다른 계정, 요청 검사, Google(상태별 결과, HTTP 상태, 시간 초과, 상품 목록 누락, 확인 호출, 확인 실패 뒤 작업자 재시도·이미 확인된 결제·3일 기한, 원장 장애에도 작업자 유지, 주문 금액), App Store(서명·체인·OID·환경·번들·환불·만료 인증서·온라인 검사, 신뢰하지 않는 체인의 AIA·OCSP 주소로 요청하지 않음, Mac App Store 공유 원장), 서버 알림(Pub/Sub OIDC 토큰 검증, 전체·일부 환불의 누적 수량, 중복·순서 뒤바뀜, 처리기 실패 뒤 재시도, 구독 상태·계정·연결 토큰, App Store 환불·환불 취소·철회·구독 상태 1~5, 서명·번들·환경·앱 ID), 원장 환불 규칙, 시작 검사.
