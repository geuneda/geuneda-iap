# Changelog
이 패키지의 모든 주요 변경사항은 이 파일에 기록됩니다.

이 형식은 [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)를 기반으로 하며,
이 프로젝트는 [Semantic Versioning](http://semver.org/spec/v2.0.0.html)을 따릅니다.

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
