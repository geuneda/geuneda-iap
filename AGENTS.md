# Geuneda.Iap - AI 에이전트 가이드

> **관련 파일**: `CLAUDE.md`는 이 파일을 감쌉니다 — `CLAUDE.md`가 아니라 `AGENTS.md`를 편집하세요. `README.md`는 사용자 대상 문서입니다.

## 1. 패키지 개요
- **패키지**: `com.geuneda.iap`
- **Unity**: 2021.3+ (Unity IAP 어댑터 `com.geuneda.iap.unity` 는 2022.3+)
- **의존성**: `com.geuneda.core` 0.4.0(ModuleLogger, Result, `IMonotonicClock`, `MainThreadDispatcher`), `com.cysharp.unitask`. analytics 는 선택입니다(있으면 `Geuneda.Iap.Analytics` 가 `versionDefines` 의 define `GEUNEDA_IAP_ANALYTICS` 로 켜짐. 이 define 은 그 asmdef 안에서만 정의되므로 다른 어셈블리에서 `#if` 로 쓰지 않습니다). http 도 선택입니다(있으면 `Geuneda.Iap.Http` 가 define `GEUNEDA_IAP_HTTP` 로 켜짐). 결제 서버(`Server~`)는 .NET 10, ASP.NET Core, Google.Apis.Auth 1.77.0 입니다
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
| `Runtime/Protocol/` | 결제 서버 와이어 형식(요청·응답 DTO, 이유 코드, 스토어 이름, 경로). `Server~` 가 링크해 함께 컴파일하므로 core·UniTask·UnityEngine 을 쓰지 않습니다(`IapProductType.cs` 도 링크됨) |
| `Runtime/IapRedeemResults.cs` | 주문 → 요청, 응답 → 지급 결과 변환(상태를 모르면 `RetryLater`) |
| `Runtime/Http/` | `HttpPurchaseFulfiller<TGrant>`(선택 어셈블리, http 있을 때 `GEUNEDA_IAP_HTTP`) |
| `Server~/` | .NET 10 솔루션 `Geuneda.Iap.Server.slnx`: 라이브러리(`Redeem/` 처리 순서·엔드포인트, `Ledger/`, `Grants/`, `Verification/` 계약, `GooglePlay/`, `AppStore/`, `Fake/`), xUnit 테스트, 샘플 서버 |

## 3. 규칙

- **확정 순서(불변식)**: 지급자가 `Granted`·`AlreadyGranted` 를 돌려준 주문만 `IIapStore.ConfirmAsync` 합니다. `RetryLater`·예외·시간 초과·정의되지 않은 상태(`default`)는 거래를 열어 두고 백오프, `Rejected` 는 열어 두고 자동 재시도하지 않음. 이 순서를 바꾸면 `IapServicePurchaseTest` 의 확정 순서·재시도 테스트가 실패합니다(되돌림 실험으로 확인, 2026-10-15).
- **확정만 재시도**: 지급이 끝난 항목(`OrderEntry.Granted`)은 지급자를 다시 부르지 않습니다. `OrderSettled` 는 항목마다 한 번(`Announced`)이고 `PurchaseConfirmed(PurchaseSettlement)` 는 거래마다 한 번인 매출 지점이고 근거는 하나입니다: 지급 결과가 결제 서버 응답(`IIapRedeemResponse`)이면 이 기기가 거래를 닫았고 지급이 `Granted`(서버 원장의 첫 지급)일 때(스토어가 `AlreadyConfirmed` 여도, 서버가 Google 결제를 먼저 확인함), 그 밖은 스토어가 `Confirmed` 를 줄 때. 두 근거를 섞으면 여러 기기 경합에서 두 번 잡힙니다(리뷰 2회차: B 가 `AlreadyGranted`+`Confirmed`, A 가 `Granted`+`AlreadyConfirmed`). 첫 응답 유실 때의 누락은 클라이언트만으로 막을 수 없어 서버 원장 집계를 권합니다. 어댑터는 시간 초과 뒤 늦게 온 확정 성공을 다음 확정 요청에 돌려줘야 합니다(`IIapStore.ConfirmAsync` 계약).
- **매출 금액**: `IapRevenueLogger` 는 `paidAmount`(`IapPaidAmount` 총액+통화, 기본 `DefaultPaidAmount`: 지급 결과가 결제 서버 응답 `IIapRedeemResponse` 면 테스트 결제·`Revoked` 는 null, 서버 총액이 있으면 그것. 그 밖은 구독 null, 표시 가격 × 서버 수량과 상품 조회 통화)로 정하고 null 이면 기록하지 않습니다. 이벤트는 총액 ÷ 서버 수량을 단가로, 서버 수량(그 밖은 1)을 수량으로 씁니다(App Store `price` 와 Google 주문 `total` 은 수량을 포함한 총액, Apple 문서로 확인). 표시 가격은 무료 체험·할인·프로모션과 다르고(리뷰 1회차), 복구한 주문은 상품 조회 통화가 결제 통화와 다를 수 있습니다(리뷰 2회차).
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
- **서버 처리 순서(불변식)**: 원장 확인 → 스토어 검증 → 원장 트랜잭션 안의 지급 → 확인(Google). 원장에 이미 있으면 스토어를 부르지 않습니다. 지급자 결과가 `Granted` 일 때만 기록하고, `Rejected`·`RetryLater`·예외·정의되지 않은 값은 기록하지 않습니다(롤백). 지급자·원장 예외는 잡지 않아 500 이 되고 클라이언트가 다시 보냅니다. 스토어 확인(`AcknowledgeAsync`)은 커밋 뒤라 요청 토큰을 넘기지 않습니다.
- **확실하지 않으면 닫지 않음**: 검증기가 모르는 상태·응답·네트워크 문제는 `Unavailable`(`RetryLater`)입니다. `Revoked`(거래만 닫음)와 `Invalid`(거절)는 스토어가 분명히 답했을 때만 씁니다. 잘못 닫으면 지급이 누락되고, 잘못 거절해도 이번 실행에서는 다시 시도하지 않습니다. 클라이언트 변환(`ToFulfillmentResult`, HTTP 지급자)도 같아서 5xx·본문 오류·모르는 상태는 `RetryLater` 입니다.
- **원장 스토어**: 원장 키는 요청의 스토어 이름이 아니라 검증기의 `LedgerStore` 입니다. App Store 와 Mac App Store 는 거래 ID 공간이 같아 `AppleAppStore` 하나로 씁니다(다른 이름으로 같은 거래가 오면 두 번 지급되던 것을 리뷰 전에 발견, `AppStoreTest`).
- **Google 확인과 소비**: 서버는 지급 뒤 확인(acknowledge)만 하고 소비하지 않습니다. Unity IAP 5.4.4 는 확인된 소모성을 여전히 미확정 주문으로 다뤄 클라이언트가 소비하고, 확인된 비소모성·구독을 다시 확정하면 DuplicateTransaction 실패를 주는데 어댑터가 `AlreadyConfirmed` 로 바꿉니다(소스로 확인, `GooglePurchaseConverter`·`GoogleFinishTransactionUseCase`). 서버가 소비하면 클라이언트 확정이 '구매 없음' 예외로 실패합니다.
- **Apple 검증**: Apple 공식 라이브러리 규칙을 그대로 따릅니다(체인 길이 3, 잎 OID 1.2.840.113635.100.6.11.1, 중간 OID 1.2.840.113635.100.6.2.1, 온라인이면 지금 시각과 OCSP·아니면 `signedDate`, 폐기 상태를 모르면 다시 시도). 내장 루트는 Apple Root CA - G3(SHA-256 지문 테스트로 고정). x5c 의 루트는 보지 않고 신뢰 루트로 체인을 만듭니다.
- **체인 검증 두 단계(SSRF)**: 1단계는 `DisableCertificateDownloads` + 폐기 확인 없음으로 신뢰 체인만 만들고, 통과한 체인에만 2단계 OCSP 를 묻습니다. JWS 서명·OID 는 공격자가 자체 인증서로 통과할 수 있어, 내려받기를 켜 두면 신뢰하지 않는 인증서의 AIA 주소(내부망)로 서버가 요청합니다(macOS 에서 이 설정을 빼면 수신기에 3번 연결됨을 확인, 리뷰 1회차). `Untrusted_chain_does_not_make_the_server_fetch_its_urls` 가 지킵니다.
- **확인 대기와 작업자**: 확인할 결제(Google 이 `ACKNOWLEDGED` 라고 분명히 답하지 않은 것, 상태 없음·모르는 값 포함 — 리뷰 2회차)는 지급과 같은 트랜잭션에 `AcknowledgementDueAt`(지급 시각)으로 남기고, 커밋 직후 `AcknowledgeAsync` 가 true 면 `MarkAcknowledgedAsync` 합니다. `PendingAcknowledgementWorker`(AddGooglePlay 가 등록하는 호스트 서비스)가 시작할 때와 간격마다 시각이 된 기록을 배치(100)로 모두 처리하고, 실패한 기록은 `DeferAcknowledgementAsync` 로 다음 간격 뒤로 미뤄 다음 배치로 넘어갑니다(리뷰 2회차: 앞 100건 실패가 뒤를 막았음, `Failing_records_do_not_block_later_ones`). 회차당 최대 100배치. `Defer` 는 이미 해제된 기록을 대기로 되돌리지 않으므로 커밋 직후 확인과 작업자가 겹쳐도 됩니다. 한 회차의 예외는 Error 로그 후 다음 회차(BackgroundService 예외는 기본으로 호스트를 끄므로 이 루프만 예외를 잡음). Google 확인은 거절되면 상태를 다시 읽어 이미 확인·소비·취소면 끝내고, 결제 뒤 3일이 지나면 그만둡니다(리뷰 1회차: 확인 실패를 복구할 경로가 없었음).
- **서버 알림의 환불**: 원장의 누적 환불 수량(`RefundedQuantity`)을 스토어가 알려 준 값으로 맞추는 상태 기반 반영입니다(`UpdateRefundAsync` → `RefundApplier`). 콜백은 수량이 바뀔 때만, 늘면 `OnRefundedAsync`, 줄면 `OnRefundReversedAsync`. 순서 방식(`RefundUpdateMode`): App Store 는 `EventOrdered`(이벤트 시각, `RefundedAt` 보다 이르면 `Stale`, 수량이 같아도 늦은 이벤트면 시각을 앞당김 — 앞당기지 않으면 먼저 온 환불 취소 뒤의 늦은 환불이 최종 상태를 뒤집음, 리뷰 2b 1회차), Google 은 `NeverDecrease`(조회한 지금 상태라 이벤트 시각으로 순서를 정할 수 없고, 환불 취소가 없으므로 줄이지 않음 — 늦게 끝난 앞 조회가 수량을 줄여 되돌려 주던 것을 막음). 원장에 없는 거래의 환불은 계정 없는 지급 전 환불 기록(`IsGranted` false)으로 남기고 200, 지급 요청이 그 기록을 보고 환불 수량만큼 빼고 지급하거나 `Revoked` 로 닫습니다(리뷰 2b 2회차: 재전송 기간 방식은 기간 뒤 환불 전 JWS 재생으로 무료 지급, 503 대기가 구독 권한 해제까지 막음). 진행 중 지급과 겹친 환불은 같은 거래 잠금에서 커밋을 기다립니다. 모르는 환불·상품 종류, 상태 없거나 모르는 구독, 상품 목록 없는 응답, 원장에 없는데 Google 구매 조회가 실패하면 503(회수·무시하지 않음). Apple 구독 소유자는 `FindByOriginalTransactionAsync`(갱신 거래만 지급한 경우, 지급 기록만).
- **구독 상태 순서**: 원장의 `UpdateSubscriptionAsync` 가 구독마다 잠그고 저장된 마지막 상태로 다음 상태를 만들어, 관측 시각이 저장된 것보다 이르면 넘기지 않습니다. Google 은 조회를 이 잠금 안에서 하고 `ObservedAt` 을 저장된 것보다 늦게 맞춥니다(조회 완료 시각만으로는 늦게 끝난 옛 조회가 새 상태를 덮음, 리뷰 2b 2회차 — 잠금을 빼면 `Concurrent_subscription_reads_cannot_bring_back_an_older_state` 가 실패함을 확인). InMemory 의 구독 잠금은 거래 잠금과 따로 둡니다. 지급 요청에서 구독 상태를 처리기로 넘기지 않습니다(리뷰 2b 3회차: 그렇게 했더니 지급 검증 조회가 잠금을 우회해 옛 상태로 덮고, App Store 거래 서명의 불완전한 상태가 알림의 자동 갱신·유예 정보를 지우고, 전달 실패를 영속 재처리할 수 없고, 과거 거래의 중복 요청이 소유 계정을 되돌림). 계정 없는 갱신의 연결은 게임 몫이고 `VerifiedPurchase.SubscriptionId`·`ObservedAt` 을 줍니다.
- **구독 환불과 철회**: Google 구독 `voidedPurchaseNotification` 은 상태만 넘기고 지급을 회수하지 않습니다(토큰 하나에 주문이 여럿이라 지급 전에는 처음 주문인지 알 수 없음). `SUBSCRIPTION_REVOKED` 만 지급을 회수하거나 지급 전 환불 기록을 남깁니다.
- **모두 환불된 거래의 지급 요청**: 지급자를 부르지 않고 계정만 기록(원장 콜백에 `Granted(null)`)한 뒤 `Revoked` 로 답하고 확인 대기를 지웁니다. 계정 없는 지급 전 환불 기록을 그대로 두면 나중의 환불 취소를 돌려줄 계정이 없습니다(리뷰 2b 3회차). App Store 의 모르는 구독 `status` 는 503.
- **알림으로 지급·확인하지 않음**: 결제 알림만으로는 계정을 모르고, 지급하지 않은 결제를 서버가 확인하면 Google 자동 환불(지급 못 받은 결제의 마지막 안전망)을 막습니다. 알림 처리에서 acknowledge 를 부르는 코드를 넣지 않습니다(테스트가 `:acknowledge` 호출 0 을 확인).
- **알림 처리 객체는 등록하지 않음**: `GooglePlayNotificationProcessor`·`AppStoreNotificationProcessor`·`RefundApplier` 는 엔드포인트가 요청마다 `ActivatorUtilities` 로 만듭니다. DI 에 등록하면 한 스토어만 쓰는 앱이 개발 환경의 Build 검사(ValidateOnBuild)에서 다른 스토어 의존성 때문에 실패합니다.
- **Pub/Sub 토큰 시각**: Google.Apis.Auth 1.77 의 `SignedTokenVerificationOptions.Clock` 은 공개 멤버가 아니라 토큰 시각은 시스템 시계로 확인합니다. 테스트 토큰(`TestPubSubIssuer`)은 실제 현재 시각으로 발급하고, 인증서는 `NotificationCertificatesUrl` 의 로컬 JWKS 로 받습니다(실제 검증 경로).
- **응답을 믿을 수 없으면 다시 시도**: Google 응답에 상품 목록이 없거나 상품 ID 가 빈 항목이 있으면 상품 불일치로 거절하지 않고 `Unavailable` 입니다(리뷰 1회차).
- **시각**: 서버는 `TimeProvider` 를 씁니다(테스트는 `FakeTimeProvider`). Google RFC 3339 시각은 소수점 7자리(틱)로 자릅니다(나노초가 옴).
- **결과 코드**: 7000 블록(core README 표에 등록). 7001~7099 는 클라이언트 결과 코드, 7100~7199 는 결제 서버 이유 코드(`IapRedeemCodes`)입니다. 결제됐을 수 있는 코드(Timeout, Cancelled, Deferred, DuplicateTransaction, FulfillmentPending)는 README 표에 표시하고 '결제 실패' 안내 금지를 문서화합니다.

## 4. 테스트

- `Tests/EditMode/` (`Geuneda.Iap.Tests.EditMode`): `FakeStore`(스토어 상태를 들고, 확정하면 미확정 목록에서 뺌), `ScriptedFulfiller`(결과 순서·붙잡기), `ManualDelay`(길이별로 지연을 골라 끝냄 — 시간 설정이 길이마다 달라야 함), `FakeMonotonicClock`, `Captured<T>`(풀링된 UniTask 결과를 한 번만 읽음). 가짜가 바로 끝나는 UniTask 를 돌려주므로 대부분의 흐름이 호출 안에서 끝납니다. 구매·재시도·백오프·확정만 재시도·시간 초과·늦은 결제·승인 대기·중복 거래·미확정 주문 재사용·중복 제거·큐 순서·오용·처리기 예외·다른 스레드 완료(UnityTest), 복구·재조회 순서·관찰자·복귀·초기화 재시도·복원·보유, 종료·설정 검증·스레드.
- `Tests/EditMode/Analytics/` (`Geuneda.Iap.Analytics.Tests.EditMode`): 기본 테스트 어셈블리의 `IapServiceTestBase` 를 참조합니다.
- `Tests/EditMode/Http/` (`Geuneda.Iap.Http.Tests.EditMode`): 가짜 `IHttpTransport` 로 응답 변환(JSON 설정은 지급자마다 비공개로 둠, 공유 가변 static 금지), `SampleServerInteropTest`(명시 실행, `GEUNEDA_IAP_INTEROP_URL`)는 샘플 서버와 실제로 주고받습니다.
- `Server~/tests/Geuneda.Iap.Server.Tests` (xUnit, `dotnet test "Server~/Geuneda.Iap.Server.slnx"`): 127.0.0.1 빈 포트의 실제 Kestrel(`IapTestServer`), 가짜 Android Publisher API(`FakeGooglePlay`, `HttpMessageHandler`), Apple 과 같은 모양의 테스트 인증서 서명기(`TestAppleSigner`), 대본 지급자(`TestGrantor`). 개발 환경은 `Build()` 의 DI 검사가 먼저 실패하므로 `MapGeunedaIap` 의 시작 검사는 Production 환경으로 확인합니다. 확인 작업자는 테스트가 `RunOnceAsync` 로 직접 돌리고, 시간을 앞당기는 테스트는 간격을 길게 두어 작업자가 끼어들지 않게 합니다. 500 응답 본문은 개발자 예외 페이지(JSON 아님)입니다.
- analytics·http 가 없는 프로젝트에서 선택 어셈블리가 빠지고 컴파일되는지는 core·iap 만 넣은 임시 프로젝트로 확인했습니다(2026-10-15). 선택 어셈블리의 참조나 `versionDefines` 를 바꾸면 다시 확인합니다.

## 5. 문서

공개 API 를 바꾸면 `README.md`, `CHANGELOG.md`, 호스트 `claude-skills/geuneda-iap/` 스킬과 `claude-skills/geuneda-frameworks/`(패키지 표, `references/api.md` 카드)를 함께 고치고 `claude-skills/install.sh geuneda-iap`, `claude-skills/install.sh geuneda-frameworks` 로 각각 설치합니다(스크립트는 인자 하나만 받음). `Runtime/Protocol` 을 바꾸면 서버와 클라이언트가 함께 바뀌므로 서버 테스트와 상호 운용 테스트를 함께 돌립니다. 어댑터 패키지가 `IIapStore` 계약에 의존하므로 계약을 바꾸면 `com.geuneda.iap.unity` 와 버전을 함께 올립니다.
