using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 인앱 결제 서비스입니다. 결제한 주문은 게임의 지급자(<see cref="IPurchaseFulfiller"/>)가 지급을 확인한 뒤에만 스토어 거래를 닫아,
	/// 결제했는데 지급되지 않은 주문이 스토어에 남아 다시 처리되게 합니다.
	/// </summary>
	/// <remarks>
	/// 메인 스레드에서만 부릅니다. 정상 실패는 결과 코드(<see cref="IapResultCodes"/>)로, 오용(정의에 없는 상품, 종료 뒤 호출)은 예외로 알립니다.
	/// <see cref="IDisposable.Dispose"/> 하면 기다리는 호출은 취소되고 열린 거래는 스토어에 남아 다음 실행에서 처리됩니다(스토어 어댑터는 따로 정리).
	/// </remarks>
	public interface IIapService : IDisposable
	{
		/// <summary>스토어 연결과 상품 조회가 끝났는지</summary>
		bool IsInitialized { get; }

		/// <summary>스토어에서 받은 상품. 초기화 전에는 비어 있습니다</summary>
		IReadOnlyList<IapProduct> Products { get; }

		/// <summary>결제창이나 복원 요청이 열려 있는지(외부 화면 동안 앱이 멈춥니다)</summary>
		bool IsStoreUiOpen { get; }

		event Action ProductsUpdated;

		/// <summary>
		/// 결제창·복원 요청이 열리거나(true) 닫혔습니다(false). 외부 화면 동안 앱이 멈춰 실시간 연결의 핑 시간 초과로 끊기지 않게
		/// 열린 동안만 핑 검사를 멈추거나, 닫힌 직후 토큰을 확인하는 데 씁니다(레퍼런스는 결제창 동안만 핑퐁을 멈춤)
		/// </summary>
		event Action<bool> StoreUiChanged;

		/// <summary>
		/// 주문의 지급이 끝났습니다. 주문마다 한 번입니다. 구매 화면 밖에서 끝난 지급(이전 실행의 결제, 시간 초과 뒤 늦게 온 결제, 승인 대기 뒤 승인)의
		/// 보상 안내는 여기서 <see cref="PurchaseSettlement.IsAwaited"/> 가 false 인 것만 처리하세요
		/// </summary>
		event Action<PurchaseSettlement> OrderSettled;

		/// <summary>주문 처리가 끝나지 못했습니다(다시 시도 예정 포함). 원격 로그로 보내 지급 누락을 추적하세요</summary>
		event Action<IapOrderFailure> OrderFailed;

		/// <summary>
		/// 스토어가 거래 확정을 성공으로 알렸습니다. 거래마다 한 번이라 매출 기록은 여기서만 합니다
		/// (이미 다른 곳에서 닫힌 거래는 오지 않음). 지급자 응답(<see cref="PurchaseSettlement.Fulfillment"/>)에 서버가 확인한 실결제 금액을 담아 쓸 수 있습니다.
		/// analytics 가 있으면 <c>Geuneda.Iap.Analytics.IapRevenueLogger</c> 가 구독합니다
		/// </summary>
		event Action<PurchaseSettlement> PurchaseConfirmed;

		bool TryGetProduct(string productId, out IapProduct product);

		/// <summary>
		/// 스토어에 연결하고 상품을 조회합니다. 성공하면 스토어에 남은 미확정 주문의 복구를 뒤에서 시작합니다.
		/// 여러 번 불러도 한 번만 진행하고, 실패하면 다시 부를 수 있습니다
		/// </summary>
		UniTask<Result> InitializeAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// 상품을 사고 지급과 거래 확정까지 끝낸 결과를 돌려줍니다. 같은 상품의 미확정 주문이 이미 있으면 결제창을 열지 않고 그 주문을 먼저 지급합니다.
		/// </summary>
		/// <param name="productId">정의한 상품 ID. 정의에 없으면 <see cref="ArgumentException"/></param>
		/// <param name="payload">지급자에 넘길 게임 데이터(<see cref="IapOrder.Payload"/>)</param>
		/// <param name="cancellationToken">기다리기만 그만둡니다. 결제는 계속될 수 있고, 끝나면 뒤에서 지급합니다</param>
		UniTask<Result<PurchaseSettlement>> PurchaseAsync(string productId, string payload = null, CancellationToken cancellationToken = default);

		/// <summary>
		/// 스토어를 다시 조회해 미확정 주문을 모두 지급·확정합니다(거절했던 주문도 다시 시도). 로그인 직후, 재연결 뒤, 복원 버튼에서 부르세요.
		/// 초기화와 포그라운드 복귀 때는 서비스가 스스로 부릅니다
		/// </summary>
		UniTask<Result<RecoverySummary>> RecoverAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// 이전 구매를 복원합니다(App Store 는 Apple ID 확인 화면이 뜰 수 있음). 미확정 주문을 처리한 뒤, 보유 중인 비소모성·구독 주문을 돌려줍니다.
		/// 게임 계정에 권한을 다시 주려면 이 주문을 서버로 보내 확인하세요
		/// </summary>
		UniTask<Result<IReadOnlyList<IapOrder>>> RestoreAsync(CancellationToken cancellationToken = default);

		/// <summary>스토어 계정 기준 보유 상태를 묻습니다. 정의에 없는 상품이면 <see cref="ArgumentException"/></summary>
		UniTask<Result<IapEntitlementStatus>> CheckEntitlementAsync(string productId, CancellationToken cancellationToken = default);
	}
}
