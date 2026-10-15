using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 스토어 SDK 를 감싸는 어댑터 계약입니다. Unity IAP 어댑터는 <c>com.geuneda.iap.unity</c> 패키지에 있습니다.
	/// 서비스(<see cref="IapService"/>)가 순서·중복·재시도·시간 제한을 맡으므로, 어댑터는 SDK 호출과 형식 변환만 합니다.
	/// </summary>
	/// <remarks>
	/// <para>이벤트는 메인 스레드에서 올립니다(다른 스레드에서 오면 서비스가 메인 스레드로 넘깁니다).</para>
	/// <para>
	/// 비동기 메서드는 정상 실패를 결과 코드로 돌려줍니다. 서비스가 시간 제한을 따로 걸고, 시간이 지나면 기다리기를 그만둡니다.
	/// 토큰이 취소되면 기다리기를 그만두고 <see cref="ResultCodes.Cancelled"/> 를 돌려줘도 됩니다.
	/// </para>
	/// </remarks>
	public interface IIapStore
	{
		/// <summary>스토어 이름(예: GooglePlay, AppleAppStore). 지급 요청에 함께 넘어갑니다</summary>
		string Name { get; }

		bool IsConnected { get; }

		/// <summary>
		/// 미확정 주문이 왔습니다. 구매 결과, 승인 대기였던 결제의 승인, 스토어가 다시 보낸 이전 거래, 구독 갱신이 모두 여기로 옵니다.
		/// <see cref="FetchOrdersAsync"/> 로 조회한 주문은 이 이벤트로 다시 보내지 않습니다.
		/// </summary>
		event Action<IapStoreOrder> OrderPending;

		event Action<IapStorePurchaseFailure> PurchaseFailed;

		/// <summary>결제가 승인 대기로 넘어갔습니다(게임 상품 ID). 승인되면 <see cref="OrderPending"/> 이 옵니다</summary>
		event Action<string> PurchaseDeferred;

		/// <summary>연결이 끊겼습니다(설명). 서비스는 다음 작업 때 다시 연결합니다</summary>
		event Action<string> Disconnected;

		UniTask<Result> ConnectAsync(CancellationToken cancellationToken);

		/// <summary>
		/// 상품을 조회합니다. 일부만 받았으면 받은 것만 성공으로 돌려줍니다. SDK 가 자체 재시도를 한다면 끄세요(서비스가 시간 제한을 겁니다).
		/// </summary>
		UniTask<Result<IReadOnlyList<IapProduct>>> FetchProductsAsync(IReadOnlyList<IapProductDefinition> definitions, CancellationToken cancellationToken);

		/// <summary>스토어에 남은 주문을 다시 조회합니다. 조회 실패를 빈 목록으로 돌려주지 마세요(복구가 조용히 건너뛰어짐)</summary>
		UniTask<Result<IapStoreOrders>> FetchOrdersAsync(CancellationToken cancellationToken);

		/// <summary>
		/// 결제창을 엽니다. 결과는 <see cref="OrderPending"/>, <see cref="PurchaseFailed"/>, <see cref="PurchaseDeferred"/> 중 하나로 옵니다.
		/// 이 호출 안에서 바로 올려도 됩니다.
		/// </summary>
		void Purchase(IapProductDefinition product);

		/// <summary>
		/// 거래를 닫습니다(소모성은 소비, 그 밖은 승인). 지급이 끝난 주문에만 서비스가 부릅니다. 서비스는 제한 시간이 지나면 기다리기를 그만두고 나중에 같은 거래로
		/// 다시 부릅니다. 그 사이 앞 요청이 성공했으면 다시 확정하지 말고 그 결과(<see cref="IapConfirmResult.Confirmed"/>)를, 이미 닫힌 거래면
		/// <see cref="IapConfirmResult.AlreadyConfirmed"/> 를 돌려줘야 합니다(실패를 돌려주면 서비스가 같은 거래를 계속 확정하려 함)
		/// </summary>
		UniTask<IapConfirmResult> ConfirmAsync(IapStoreOrder order, CancellationToken cancellationToken);

		/// <summary>이전 구매를 복원합니다(App Store 의 복원 요청). 따로 할 일이 없는 스토어는 바로 성공을 돌려줍니다</summary>
		UniTask<Result> RestoreAsync(CancellationToken cancellationToken);

		UniTask<Result<IapEntitlementStatus>> CheckEntitlementAsync(IapProductDefinition product, CancellationToken cancellationToken);
	}
}
