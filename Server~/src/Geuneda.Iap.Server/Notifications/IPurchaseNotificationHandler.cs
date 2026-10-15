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
	/// 구독 변경은 그 시점의 상태 스냅숏이고, 원장(<see cref="IPurchaseLedger.UpdateSubscriptionAsync"/>)이 구독마다 하나씩 관측 시각 순서로만 넘기므로 받은 순서대로 반영하면 됩니다.
	/// 지급 요청 전에 온 갱신은 <see cref="SubscriptionUpdate.AccountId"/> 가 null 이고 다시 오지 않으므로, <see cref="SubscriptionUpdate.SubscriptionId"/> 로 저장해 두고 지급자가
	/// <see cref="VerifiedPurchase.SubscriptionId"/> 로 계정에 연결합니다(README '구독 계정 연결'). 같은 트랜잭션에서 불리며 예외를 던지면 롤백하고 500 입니다.
	/// </para>
	/// </remarks>
	public interface IPurchaseNotificationHandler
	{
		/// <summary>지급한 거래가 환불·철회됐습니다. <see cref="PurchaseRefund.Quantity"/> 만큼 회수합니다(재화 차감, 권한 해제 등).</summary>
		Task OnRefundedAsync(PurchaseRefund refund, CancellationToken cancellationToken);

		/// <summary>환불이 취소됐습니다(App Store 분쟁 뒤 환불 철회). <see cref="PurchaseRefund.Quantity"/> 만큼 다시 지급합니다.</summary>
		Task OnRefundReversedAsync(PurchaseRefund refund, CancellationToken cancellationToken);

		/// <summary>구독 상태가 바뀌었을 수 있습니다(갱신, 자동 갱신 끔, 결제 실패, 만료, 철회 등). 상태와 만료 시각으로 권한을 맞춥니다.</summary>
		Task OnSubscriptionChangedAsync(SubscriptionUpdate update, CancellationToken cancellationToken);
	}
}
