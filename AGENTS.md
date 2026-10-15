# Geuneda.Iap - AI 에이전트 가이드

> **관련 파일**: `CLAUDE.md`는 이 파일을 감쌉니다 — `CLAUDE.md`가 아니라 `AGENTS.md`를 편집하세요. `README.md`는 사용자 대상 문서입니다.

## 1. 패키지 개요
- **패키지**: `com.geuneda.iap`
- **Unity**: 2021.3+ (Unity IAP 어댑터 `com.geuneda.iap.unity` 는 2022.3+)
- **의존성**: `com.geuneda.core` 0.4.0(ModuleLogger, Result, `IMonotonicClock`, `MainThreadDispatcher`), `com.cysharp.unitask`. analytics 는 선택입니다(있으면 `Geuneda.Iap.Analytics` 가 `versionDefines` 의 define `GEUNEDA_IAP_ANALYTICS` 로 켜짐. 이 define 은 그 asmdef 안에서만 정의되므로 다른 어셈블리에서 `#if` 로 쓰지 않습니다)
- **스토어 SDK**: 결정 D3 에 따라 어댑터 패키지(`com.geuneda.iap.unity`)로 붙입니다. 이 패키지에 SDK 참조를 넣지 않습니다
- **출처**: Lucky Defense 의 Percent Purchase 와 게임 상점 코드(로드맵 4-2). 따른 것: 서버 지급 뒤 Confirm, '이미 지급' 응답도 Confirm, 구매·복구 상호 배제, 같은 상품 미확정 주문 먼저 사용, 중복 거래면 재조회, 결제창 동안만 핑퐁 중지(이벤트로 노출), 확정 성공 한 곳에서 매출 기록. 보완한 것(레퍼런스 약점): 복구 때 캐시가 아니라 항상 재조회, 앱 복귀·백오프 재시도(레퍼런스는 로비 진입·복원 버튼뿐), 시간 초과 뒤 늦게 끝난 결제도 지급(레퍼런스는 캐시에 안 들어가 재시작 전까지 누락), 확정만 실패하면 성공으로 알리고 확정만 재시도(레퍼런스는 실패 토스트), 원인별 결과 코드와 실패 이벤트(레퍼런스는 90000 하나, 원격 로그 없음), 실패 이유 명시 매핑(레퍼런스는 정수 7 을 중복 거래로 비교했으나 Unity IAP v5 에서 7 은 검증 실패). 사용자 게임(SpellDefense)의 교훈: 상품 조회 뒤에만 구매 조회, 종류를 모르는 주문은 확정 금지, Unity 자동 재전달 끄기, 결제창 콜백 시간 제한 뒤 늦은 결제 지급

## 2. 구조

| 파일 | 역할 |
|------|------|
| `Runtime/IapService.cs` | 공개 API(초기화, 구매, 복구, 복원, 보유 조회), 연결 single-flight, 결제창 시간 제한, 종료, 일시정지·복귀 |
| `Runtime/IapService.Orders.cs` | 스토어 이벤트 처리, 주문 수락(`Accept`, 거래 ID 로 하나의 항목), 늦은 결제 연결(`_unclaimed`), 재조회 single-flight(`FetchRequest` 와 관찰자), 복구(`RecoverCoreAsync`), 중복 거래 해소 |
| `Runtime/IapService.Lane.cs` | 지급·확정 큐(`RunLaneAsync`, 주문 하나씩), 백오프와 깨우기 타이머 하나, 바깥 코드 호출 보호(`CallAsync`: 시간 제한·예외·메인 스레드 복귀), 이벤트 처리기 격리 |
| `Runtime/Internal/` | `OrderEntry`(거래 하나의 상태), `PurchaseRequest`, `Waiter`(호출자 한 명의 결과 자리), `CallResult`, `IapLifecycle`(숨은 수명 컴포넌트) |
| `Runtime/Analytics/` | `IapRevenueLogger`(선택 어셈블리) |

## 3. 규칙

- **확정 순서(불변식)**: 지급자가 `Granted`·`AlreadyGranted` 를 돌려준 주문만 `IIapStore.ConfirmAsync` 합니다. `RetryLater`·예외·시간 초과·정의되지 않은 상태(`default`)는 거래를 열어 두고 백오프, `Rejected` 는 열어 두고 자동 재시도하지 않음. 이 순서를 바꾸면 `IapServicePurchaseTest` 의 확정 순서·재시도 테스트가 실패합니다(되돌림 실험으로 확인, 2026-10-15).
- **확정만 재시도**: 지급이 끝난 항목(`OrderEntry.Granted`)은 지급자를 다시 부르지 않습니다. `OrderSettled` 는 항목마다 한 번(`Announced`)이고 `PurchaseConfirmed(PurchaseSettlement)` 는 스토어가 `Confirmed` 를 줄 때만입니다(`AlreadyConfirmed` 는 매출 없음). 어댑터는 시간 초과 뒤 늦게 온 확정 성공을 다음 확정 요청에 돌려줘야 합니다(`IIapStore.ConfirmAsync` 계약).
- **매출 금액**: `IapRevenueLogger` 는 `paidAmount`(`IapPaidAmount` 금액+통화, 기본 `DefaultPaidAmount`: 구독 null, 그 밖 표시 가격과 상품 조회 통화)로 정하고 null 이면 기록하지 않습니다. 표시 가격은 무료 체험·할인·프로모션과 다르고(리뷰 1회차), 복구한 주문은 상품 조회 통화가 결제 통화와 다를 수 있습니다(리뷰 2회차).
- **버려진 호출의 토큰**: `CallAsync` 가 시간 초과로 버린 호출의 linked CTS 는 호출이 끝날 때 `InvokeAsync` 의 finally 가 해제합니다(서비스 수명 토큰에 등록이 쌓이지 않게). 취소와 해제가 엇갈리면 `CancelQuietly` 가 `ObjectDisposedException` 을 무시합니다.
- **큐**: 지급·확정은 한 줄 큐에서 하나씩. 구매가 만든 주문은 큐 앞에 넣습니다. 초기화 전에는 큐를 돌리지 않습니다(상품 정보가 없어 가격이 0 인 주문이 됨). 지급자에 넘기는 `IapOrder` 는 `ProcessAsync` 가 지급 시도마다 최신 `StoreOrder`·상품 정보로 다시 만듭니다. `OrderState` 의 기본값은 `New` 입니다(기본값이 `Queued` 이면 새 주문이 큐에 들어가지 않음, 2026-10-15 실수).
- **재조회 결과 자리**: 재조회 결과가 필요한 쪽(복구, 중복 거래)은 `FetchOrdersAsync(observer)` 의 관찰자 안에서 결과 자리를 겁니다. 조회가 바로 끝나면 돌려받은 태스크를 기다리기 전에 큐가 주문을 닫아 버리기 때문입니다. `RunFetchAsync` 는 수락 → 관찰자 → 결과 → 큐 순서입니다. 조회 중에 온 요청은 다음 조회(요청 뒤에 시작한 조회)의 결과를 받습니다.
- **닫은 거래의 재전달**: 이번 실행에서 닫은 거래가 다시 오면 `Accept` 가 `ignored` 로 알려, 같은 상품의 새 구매가 결제창을 기다리는 중이어도 그 구매를 끝내지 않습니다(리뷰 3회차).
- **늦은 결제**: 결제창이 답하지 않아 기다리기를 그만두면(시간 초과·취소) 뒤에서 복구(재조회)를 시작합니다. 스토어에는 결제가 있는데 이벤트만 놓쳤을 수 있고, 결제창에서 돌아온 복귀는 재조회를 건너뛰었기 때문입니다(리뷰 3회차). 기다리기를 그만두거나 승인 대기면 `_unclaimed[상품 ID] = 페이로드` 를 남기고, 그 상품의 새 주문이 오면 구매 경로·페이로드로 만듭니다. 같은 상품을 다시 사면 결제창을 열기 전에 `BindOpenOrderAsync`(재조회 + 관찰자)로 그 늦은 결제를 찾아 지급하고, 없을 때만 `_unclaimed` 를 지웁니다. 상품 ID 하나로 여러 미응답 구매를 가를 수 없어서입니다(2026-10-15 리뷰 1회차).
- **보류(`OrderState.Held`)**: 결제창이 열린 상품의 새 주문(또는 종류를 모르다 알게 된 주문, `QueueOrHold`)을 조회가 먼저 찾으면 지급하지 않고 `PurchaseRequest.Held` 에 둡니다. 조회가 먼저 지급·확정하면 구매 이벤트가 닫힌 거래로 와서 구매 결과와 페이로드를 잃기 때문입니다. 결제창의 답이 오면 `EndStoreWait` 가 이 구매의 것이 아닌 보류 주문을 큐에 넣고, 답 없이 끝나면(시간 초과·취소) 보류 주문이 하나일 때 그 구매의 것으로 지급합니다. 복구는 보류 주문의 결과도 기다립니다.
- **재진입**: 바깥 코드(이벤트 처리기, 대기 결과의 continuation)는 상태를 모두 바꾼 뒤 부릅니다. 결제창 결과 처리에서 구매 연결(`Bind`)과 대기 종료(`EndStoreWait`) 뒤에 `StoreUiChanged(false)` 를 올리고, 대기 결과(`Waiter.Complete`)는 그 **뒤에** 넣습니다(결과를 받은 continuation 이 동기로 새 구매를 열면, 뒤늦은 닫힘 알림이 새 결제창 상태를 덮음 — 리뷰 2회차). 결제창 열림 이벤트 뒤에는 종료·취소를 다시 확인한 다음 `_store.Purchase`(복원도 같음). 초기화 대기자는 `ProductsUpdated`·복구 시작 뒤에 끝내고 그때까지 `_initWaiters` 에 둡니다(처리기가 종료하면 Dispose 가 취소로 끝냄). 중복 거래는 닫힘 알림 뒤에 재조회를 시작하므로, 처리기가 그 사이 복구를 돌리면 이전 결제는 뒤에서 지급되고 구매 결과는 `DuplicateTransaction` 일 수 있습니다(지급 누락은 아님). `Accept` 는 바깥 코드를 부르지 않고, 종류 모르는 주문 알림은 `_pendingReports` 에 모아 결과 자리를 건 뒤 `FlushReports` 로 올립니다(2026-10-15 리뷰 1회차, `IapServiceRaceTest`).
- **기존 거래 갱신**: 같은 거래 ID 가 종류를 모르거나 다른 상품으로 다시 오면 검증한 `StoreOrder` 를 덮어쓰지 않고(덮어쓰면 확정 때 종류 모르는 원본을 넘김), `Accept` 의 `ignored` 로 알려 진행 중인 구매에도 걸지 않습니다(다른 상품 구매가 그 항목을 결과로 받음 — 리뷰 2회차). 정상 재전달은 `StoreOrder` 를 바꾸고, 지급자에 넘기는 `IapOrder` 는 시도마다 최신 `StoreOrder` 로 다시 만듭니다(진행 중인 호출의 주문은 그대로).
- **조회 재시도**: 조회가 실패하면(취소 제외, 초기화 뒤) 지급 재시도와 같은 간격으로 `ScheduleRecoveryRetry` 가 백오프 뒤 다시 복구합니다. 성공하면 초기화합니다. 중복 거래 뒤 조회 실패도 이 경로로 계속 복구합니다.
- **확정 전 재연결**: `ConfirmAsync` 는 먼저 `EnsureConnectedAsync` 합니다. 연결 실패는 확정 실패처럼 확정만 다시 시도합니다(Unity IAP 는 연결이 끊겨 있으면 재연결하지 않고 실패만 줌).
- **같은 상품 미확정 주문**: `PurchaseAsync` 는 결제창을 열기 전에 같은 상품의 열린 항목을 찾습니다. 지급 전이면 그 주문을 지급해 결과로 돌려주고, 지급 뒤 확정만 남았으면 확정을 먼저 한 뒤 결제창을 엽니다(Google 은 미소비 소모성이 있으면 새 결제를 거절). 확정이 안 되면 `ExistingPurchasePending`.
- **종류 모르는 주문**: `IapStoreOrder.IsKnownProduct` 가 false 거나 정의에 없는 상품이면 지급·확정하지 않고 초기화 뒤 한 번 Error 로그와 `OrderFailed(UnknownProduct)`. 다음 조회에서 알게 되면 처리합니다.
- **바깥 코드 보호**: 지급자·어댑터 호출은 모두 `CallAsync` 로 합니다(시간 제한 경주, 예외는 Error 로그 후 값으로, 다른 스레드에서 끝나면 메인 스레드로 복귀, 시간 초과 때 호출 토큰 취소, 취소 콜백 예외 격리). 결과 코드로 숨기는 것이 아니라 로그로 드러내고 거래를 열어 두는 격리입니다(호스트 실패 처리 규칙의 `catch (Exception)` 금지는 결과로 숨기는 경우). 시간 초과 뒤에 온 결과는 쓰지 않고 다음 시도에서 지급자에 다시 묻습니다(서버 멱등 전제).
- **이벤트 처리기**: 처리기마다 예외를 잡아 Error 로그 후 계속합니다(게임 코드가 확정·대기 결과를 끊지 않게).
- **대기 자리(`Waiter`)**: 취소 등록은 서비스가 결과를 넣기 전(`Complete`)이나 결과 없이 끝낼 때(`Release`)만 풉니다. 취소 콜백 안에서 이어진 continuation 이 등록을 풀면 UniTaskCompletionSource 잠금과 엇갈려 교착할 수 있습니다(servertime 에서 겪음). 서비스 버그는 `Fail(exception)` 으로 호출자에게 예외로 넘깁니다.
- **스레드**: 상태는 메인 스레드에서만 바꿉니다. 공개 메서드는 만든 스레드가 아니면 `InvalidOperationException`. 다른 스레드에서 온 스토어 이벤트는 `MainThreadDispatcher.Post` 로 넘깁니다(주문을 버리지 않음).
- **복귀 판정**: `HandlePause(false)` 는 `true` 를 먼저 받았을 때만 복귀로 봅니다. 결제창·복원 중의 복귀는 재조회하지 않습니다(그 흐름이 결과를 처리). 초기화가 실패한 뒤의 복귀는 초기화를 다시 합니다.
- **수명 컴포넌트**: 플레이 중에만 만들고 `HideInHierarchy` 만 붙입니다(servertime·analytics 와 같은 이유). `Dispose` 는 파괴 전에 `Unbind` 합니다.
- **수명**: `IIapService` 는 `IDisposable` 입니다(`MainInstaller.CleanDispose<IIapService>()`).
- **static 가변 상태 금지**(D2). 로거만 static 입니다.
- **결과 코드**: 7000 블록(core README 표에 등록). 결제됐을 수 있는 코드(Timeout, Cancelled, Deferred, DuplicateTransaction, FulfillmentPending)는 README 표에 표시하고 '결제 실패' 안내 금지를 문서화합니다.

## 4. 테스트

- `Tests/EditMode/` (`Geuneda.Iap.Tests.EditMode`): `FakeStore`(스토어 상태를 들고, 확정하면 미확정 목록에서 뺌), `ScriptedFulfiller`(결과 순서·붙잡기), `ManualDelay`(길이별로 지연을 골라 끝냄 — 시간 설정이 길이마다 달라야 함), `FakeMonotonicClock`, `Captured<T>`(풀링된 UniTask 결과를 한 번만 읽음). 가짜가 바로 끝나는 UniTask 를 돌려주므로 대부분의 흐름이 호출 안에서 끝납니다. 구매·재시도·백오프·확정만 재시도·시간 초과·늦은 결제·승인 대기·중복 거래·미확정 주문 재사용·중복 제거·큐 순서·오용·처리기 예외·다른 스레드 완료(UnityTest), 복구·재조회 순서·관찰자·복귀·초기화 재시도·복원·보유, 종료·설정 검증·스레드.
- `Tests/EditMode/Analytics/` (`Geuneda.Iap.Analytics.Tests.EditMode`): 기본 테스트 어셈블리의 `IapServiceTestBase` 를 참조합니다.
- analytics 가 없는 프로젝트에서 선택 어셈블리가 빠지고 컴파일되는지는 core·iap 만 넣은 임시 프로젝트로 확인했습니다(2026-10-15). 선택 어셈블리의 참조나 `versionDefines` 를 바꾸면 다시 확인합니다.

## 5. 문서

공개 API 를 바꾸면 `README.md`, `CHANGELOG.md`, 호스트 `claude-skills/geuneda-iap/` 스킬과 `claude-skills/geuneda-frameworks/`(패키지 표, `references/api.md` 카드)를 함께 고치고 `claude-skills/install.sh geuneda-iap`, `claude-skills/install.sh geuneda-frameworks` 로 각각 설치합니다(스크립트는 인자 하나만 받음). 어댑터 패키지가 `IIapStore` 계약에 의존하므로 계약을 바꾸면 `com.geuneda.iap.unity` 와 버전을 함께 올립니다.
