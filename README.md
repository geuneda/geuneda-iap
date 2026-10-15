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

`com.geuneda.analytics` 0.2.0 이상이 있으면 `Geuneda.Iap.Analytics` 어셈블리가 켜집니다(asmdef `versionDefines`).

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

매출은 스토어 확정이 성공한 한 곳(`PurchaseConfirmed`)에서만 기록합니다. 금액은 실결제 금액이어야 하므로, 서버가 영수증 검증으로 확인한 금액과 거래 통화를 지급자 응답에 담아 `IapPaidAmount` 로 돌려주세요(이전 실행의 결제를 복구하면 상품 조회 통화가 결제 때와 다를 수 있음). 생략하면 구독은 기록하지 않고(무료 체험·첫 달 할인) 그 밖은 상품 조회 때의 표시 가격과 통화를 씁니다.

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

서버가 지켜야 하는 것:

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
- 구독 만료·갱신 판정은 서버가 합니다. `CheckEntitlementAsync` 는 기기의 스토어 계정 기준입니다.
- 실제 Google Play·App Store 결제는 이 패키지 테스트에서 확인하지 않았습니다(어댑터는 Unity IAP 가짜 스토어로 확인).

## 테스트

- `Geuneda.Iap.Tests.EditMode`: 가짜 스토어·지급자·수동 지연으로 구매, 지급 재시도와 백오프, 확정만 재시도, 시간 초과와 늦은 결제, 승인 대기, 중복 거래, 같은 상품 미확정 주문, 거래 중복 제거, 복구·재조회·복원, 종류 모르는 주문, 초기화 실패와 복귀 재시도, 종료, 다른 스레드에서 끝난 지급, 재진입(처리기의 복구·종료)과 조회·결제창 경합(`IapServiceRaceTest`).
- `Geuneda.Iap.Analytics.Tests.EditMode`: 확정 성공 한 번만 매출 기록, 실결제 금액, 구독 기본 제외.
