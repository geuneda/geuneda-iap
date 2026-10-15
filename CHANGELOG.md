# Changelog
이 패키지의 모든 주요 변경사항은 이 파일에 기록됩니다.

이 형식은 [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)를 기반으로 하며,
이 프로젝트는 [Semantic Versioning](http://semver.org/spec/v2.0.0.html)을 따릅니다.

## [Unreleased]

**New**:
- 결제 서버 헬퍼 `Server~/src/Geuneda.Iap.Server`(ASP.NET Core, .NET 10): `AddGeunedaIap`(상품 목록 `IapServerProduct`) → `AddGooglePlay`·`AddAppStore`·`AddFakeStore`·`AddVerifier<T>`, `AddGrantor<T>`, `AddLedger<T>`·`AddInMemoryLedger`, 엔드포인트 `MapGeunedaIap(prefix, resolveAccountId)`(`POST {prefix}/purchases`), 직접 호출용 `IPurchaseRedeemer`
- 영수증 유일성 원장 `IPurchaseLedger`: 원장 기록과 게임 지급(`IPurchaseGrantor`)을 한 트랜잭션으로 묶음. 같은 계정의 중복은 처음 지급 응답과 함께 `AlreadyGranted`(`Duplicate`), 다른 계정은 지급 없이 `AlreadyGranted`(`OwnedByOtherAccount`). 원장에 있으면 스토어를 부르지 않음. App Store·Mac App Store 는 원장 스토어 하나(`IStoreVerifier.LedgerStore`)
- Google Play 검증(Android Publisher API `productsv2`·`subscriptionsv2`, 서비스 계정·ADC): PURCHASED 만 지급, PENDING 은 다시 시도, CANCELLED 는 지급 없이 닫음, 401·403·429·5xx·시간 초과·모르는 상태는 다시 시도. 지급 기록 뒤 서버 확인(acknowledge, 소비는 안 함)으로 3일 자동 환불 방지. 선택: 주문 API 로 실결제 금액
- App Store 검증(StoreKit 2 JWS): Apple 공식 `SignedDataVerifier` 와 같은 규칙(ES256, 인증서 3개, OID, 내장 Apple Root CA - G3, 온라인 검사), 번들 ID, 환경(샌드박스 허용 기본), 거래 ID·상품 일치, 환불·철회는 지급 없이 닫음, 실결제 금액(`price`·`currency`)
- 공유 와이어 형식(`Runtime/Protocol`): `IapRedeemRequest`, `IapRedeemResponse<TGrant>`, `IapRedeemStatus`, 이유 코드 `IapRedeemCodes`(7100~7199), `IapStoreNames`, `IapRoutes`. 변환 `IapRedeemResults.ToRedeemRequest`·`ToFulfillmentResult`(응답이 없거나 상태를 모르면 `RetryLater`)
- 선택 어셈블리 `Geuneda.Iap.Http`(com.geuneda.http 0.1.0 이상): `HttpPurchaseFulfiller<TGrant>`
- 확인 대기와 재시도: 확인할 Google 결제(확인됨이라고 분명히 답하지 않은 것)는 지급과 같은 트랜잭션에서 `PurchaseRecord.AcknowledgementDueAt` 으로 기록하고, 확인 작업자(호스트 서비스)가 시작할 때와 `AcknowledgementRetryInterval` 마다 시각이 된 기록을 모두 다시 확인(실패는 다음 간격 뒤로 미룸, 이미 확인·소비·취소면 끝, 결제 뒤 3일이면 그만둠). 원장에 `FindDueAcknowledgementsAsync`·`MarkAcknowledgedAsync`·`DeferAcknowledgementAsync`
- `IIapRedeemResponse`: 지급 결과 형식과 관계없이 서버 응답을 읽는 인터페이스

**Changed**:
- `PurchaseConfirmed`(매출 지점): 지급 결과가 결제 서버 응답(`IIapRedeemResponse`)이면 서버의 첫 지급(`Granted`)을 받은 기기가 거래를 닫을 때 한 번(스토어가 `AlreadyConfirmed` 여도, `AlreadyGranted` 면 `Confirmed` 여도 없음). 그 밖의 지급자는 전과 같이 스토어 `Confirmed` 때
- `IapRevenueLogger`: 지급 결과가 결제 서버 응답이면 기본 금액은 테스트 결제·환불된 거래를 빼고 서버가 확인한 총액·통화, 수량은 서버가 확인한 수량(이벤트 단가 = 총액 ÷ 수량, 표시 가격으로 대체할 때는 표시 가격 × 수량)

## [0.1.0] - 2026-10-15

**New**:
- `IapService`/`IIapService`: 결제한 주문을 게임의 지급자(`IPurchaseFulfiller`)가 지급을 확인한 뒤에만 스토어 거래를 닫는 인앱 결제 서비스. 구매·복원 상호 배제, 주문 하나씩 처리하는 지급·확정 큐(거래 ID 중복 제거), 지급 실패·예외·시간 초과의 백오프 재시도(5초부터 두 배씩 5분), 초기화·포그라운드 복귀·`RecoverAsync`·중복 거래 때 스토어 재조회, 결제창 시간 초과·취소·승인 대기 뒤 늦게 온 결제의 뒤 지급(페이로드 유지), 같은 상품의 미확정 주문 먼저 지급, 확정만 실패하면 확정만 재시도, 종류를 모르는 주문은 지급·확정하지 않음, 초기화 실패 시 포그라운드 복귀에 재시도
- 결제창이 열린 동안 조회가 먼저 찾은 같은 상품의 새 주문 보류, 같은 상품 재구매 때 답 없이 끝난 이전 구매의 결제 먼저 확인, 실패한 조회의 백오프 재조회, 확정 전 재연결, 이미 받은 거래의 종류 모르는 재전달과 이번 실행에서 닫은 거래의 재전달은 진행 중인 구매의 결과로 보지 않음, 결제창 답 없이 끝난 구매 뒤 재조회, 재조회한 최신 영수증으로 다음 지급 시도, 상태를 바꾼 뒤 이벤트 발행(처리기 재진입 안전)
- `IIapService : IDisposable`
- 이벤트 `OrderSettled`(주문마다 한 번, `IsAwaited`), `OrderFailed`(지급 누락 추적), `PurchaseConfirmed(PurchaseSettlement)`(매출 기록 지점), `StoreUiChanged`(결제창 열림·닫힘), `ProductsUpdated`
- `RestoreAsync`(보유 비소모성·구독 주문), `CheckEntitlementAsync`
- 결과 코드 `IapResultCodes`(7000 블록)
- 스토어 어댑터 계약 `IIapStore`(Unity IAP 어댑터는 `com.geuneda.iap.unity`)
- 선택 어셈블리 `Geuneda.Iap.Analytics`(com.geuneda.analytics 0.2.0 이상): `IapRevenueLogger` 가 스토어 확정 성공 때만 구매 표준 이벤트를 기록. 금액은 `paidAmount`(`IapPaidAmount`: 서버가 확인한 실결제 금액과 거래 통화, 기본은 구독 제외·그 밖은 표시 가격과 상품 조회 통화)
