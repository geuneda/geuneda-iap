using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 게임이 구현하는 서버 알림 처리기입니다(Google Play 실시간 개발자 알림, App Store Server Notifications V2).
	/// 환불은 원장 트랜잭션 안에서 불리므로, 회수·복구 쓰기는 지급자처럼 그 트랜잭션에 참여해야 합니다(같은 DB 연결·세션).
	/// </summary>
	/// <remarks>
	/// <para>
	/// 환불은 거래의 누적 환불 수량이 바뀔 때만 한 번 불립니다(<see cref="IPurchaseLedger.UpdateRefundAsync"/>). 같은 알림이 다시 오거나 순서가 뒤바뀌어도 두 번 회수하지 않습니다.
	/// 예외를 던지면 롤백하고 HTTP 500 으로 답해 스토어가 알림을 다시 보냅니다.
	/// </para>
	/// <para>
	/// 구독은 서버가 합친 권한 스냅숏(<see cref="SubscriptionUpdate"/>)이 바뀔 때마다 옵니다(서버 알림, 그리고 구독 지급 때는 지급 트랜잭션 안에서).
	/// 원장의 구독 잠금 안에서 합치므로 받은 것을 <see cref="SubscriptionUpdate.SubscriptionId"/> 로 그대로 저장하면 됩니다(계정은 스냅숏의 것, 지급 전이면 null 이고 지급 때 다시 옴).
	/// 같은 트랜잭션에서 불리며 예외를 던지면 롤백하고 500 입니다(구독 지급이면 지급도 롤백).
	/// </para>
	/// </remarks>
	public interface IPurchaseNotificationHandler
	{
		/// <summary>지급한 거래가 환불·철회됐습니다. <see cref="PurchaseRefund.Quantity"/> 만큼 회수합니다(재화 차감, 권한 해제 등).</summary>
		Task OnRefundedAsync(PurchaseRefund refund, CancellationToken cancellationToken);

		/// <summary>환불이 취소됐습니다(App Store 분쟁 뒤 환불 철회). <see cref="PurchaseRefund.Quantity"/> 만큼 다시 지급합니다.</summary>
		Task OnRefundReversedAsync(PurchaseRefund refund, CancellationToken cancellationToken);

		/// <summary>구독 권한 스냅숏이 바뀌었습니다(지급, 갱신, 자동 갱신 끔, 결제 실패, 만료, 철회 등). 그대로 저장해 상태와 만료 시각으로 권한을 맞춥니다.</summary>
		Task OnSubscriptionChangedAsync(SubscriptionUpdate update, CancellationToken cancellationToken);
	}
}
