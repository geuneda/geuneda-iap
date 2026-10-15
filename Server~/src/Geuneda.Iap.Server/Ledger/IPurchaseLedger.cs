using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 영수증 유일성 원장입니다. 거래 하나를 한 번만 지급하도록, 원장 기록과 게임 지급을 한 트랜잭션으로 묶습니다. 서버 알림의 환불 수량과 구독 상태도 여기에 둡니다.
	/// 게임 DB 로 구현합니다(개발·테스트용 <see cref="InMemoryPurchaseLedger"/>).
	/// </summary>
	/// <remarks>
	/// <para>
	/// 검증만 하고 지급 저장을 따로 하면, 그 사이 서버나 앱이 죽었을 때 재시도가 '이미 쓴 영수증'으로 거절되어 지급이 영구히 누락됩니다.
	/// 반대로 원장 기록 없이 지급만 하면 같은 영수증으로 여러 번 지급됩니다.
	/// </para>
	/// <para>
	/// 구현 방법(관계형 DB 예): (Store, TransactionId) 에 유일 제약을 둔 거래 표와 (Store, SubscriptionId) 를 키로 한 구독 표를 만듭니다.
	/// 지급 요청 전에 환불 알림이 오면 거래 표에 계정 없는 기록(<see cref="PurchaseRecord.IsGranted"/> false)이 생깁니다. 지급은 그 기록이 있으면 같은 행을 지급 기록으로 바꿉니다.
	/// 지급자·알림 처리기가 같은 트랜잭션을 쓰게 하려면 요청 범위(scoped) DB 컨텍스트·세션을 원장과 함께 받게 합니다.
	/// </para>
	/// <para>
	/// 동시성: 기존 행을 고치는 메서드(<see cref="RedeemAsync"/> 의 지급 전 환불 기록, <see cref="UpdateRefundAsync"/>, <see cref="UpdateSubscriptionAsync"/>)는 유일 제약만으로는
	/// 같은 키의 동시 처리를 막지 못합니다(READ COMMITTED 에서 두 요청이 같은 값을 읽고 두 번 회수). 트랜잭션 안에서 그 행을 잠그고(<c>SELECT ... FOR UPDATE</c>, 없으면 먼저 넣고 잠금)
	/// 다시 읽은 값으로 비교·콜백·저장하거나, 버전 열 조건부 갱신이 실패하면 게임 쓰기까지 전부 롤백하고 처음부터 다시 하세요. 트랜잭션 밖 <see cref="FindAsync"/> 로 읽은 객체를 비교에 쓰지 마세요.
	/// 확인 표시(<see cref="MarkAcknowledgedAsync"/>, <see cref="DeferAcknowledgementAsync"/>)와 환불 갱신은 서로의 열을 덮어쓰지 않아야 합니다.
	/// </para>
	/// </remarks>
	public interface IPurchaseLedger
	{
		/// <summary>기록을 찾습니다(지급 전 환불 기록 포함). 없으면 null 입니다. 트랜잭션 밖에서 먼저 확인할 때 씁니다(스토어 조회를 아끼려고).</summary>
		Task<PurchaseRecord> FindAsync(string store, string transactionId, CancellationToken cancellationToken);

		/// <summary>
		/// 한 트랜잭션에서 같은 키의 지급 기록이 없을 때만 <paramref name="grant"/> 를 부르고, 지급했으면 <paramref name="record"/> 에 지급 응답(<see cref="LedgerGrant.Response"/>)을 넣어 기록합니다.
		/// <paramref name="grant"/> 의 인자는 그 거래의 지급 전 환불 기록(없으면 null)이고, 지급하면 그 행을 지급 기록으로 바꾸되 환불 수량·시각(<see cref="PurchaseRecord.RefundedQuantity"/>,
		/// <see cref="PurchaseRecord.RefundedAt"/>)은 그대로 둡니다.
		/// </summary>
		/// <returns>
		/// 기록했으면 <see cref="LedgerRedeemResult.Recorded"/>(넣은 기록), 이미 지급 기록이 있으면 <see cref="LedgerRedeemResult.Duplicate"/>(있던 기록),
		/// 지급하지 않았으면 <see cref="LedgerRedeemResult.NotGranted"/>(롤백, 지급 전 환불 기록은 그대로)
		/// </returns>
		/// <remarks>
		/// 트랜잭션 안에서 지급 기록을 찾았으면 <paramref name="grant"/> 를 부르지 않고 <see cref="LedgerRedeemResult.Duplicate"/> 입니다. 동시 요청과 경쟁해 커밋 때 유일 제약에 걸렸으면
		/// <paramref name="grant"/> 를 이미 불렀더라도 트랜잭션 전체(지급 포함)를 롤백하고 처음부터 다시 합니다(이긴 쪽이 지급이면 <see cref="LedgerRedeemResult.Duplicate"/>,
		/// 지급 전 환불 기록이면 그것을 인자로 다시 <paramref name="grant"/>). <paramref name="grant"/> 가 예외를 던지면 롤백하고 예외를 그대로 던집니다.
		/// </remarks>
		Task<LedgerRedeemResult> RedeemAsync(PurchaseRecord record, Func<PurchaseRecord, CancellationToken, Task<LedgerGrant>> grant, CancellationToken cancellationToken);

		/// <summary>
		/// 스토어 확인 시각이 된 기록(<see cref="PurchaseRecord.AcknowledgementDueAt"/> 이 <paramref name="now"/> 이하)을 시각 순으로 <paramref name="limit"/> 개까지 돌려줍니다.
		/// 확인 작업자가 주기적으로 부르고, 돌려받은 기록은 모두 확인하거나 뒤로 미루므로 다음 조회는 그다음 기록을 줍니다.
		/// </summary>
		Task<IReadOnlyList<PurchaseRecord>> FindDueAcknowledgementsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

		/// <summary>스토어 확인을 끝냈다고 표시합니다(<see cref="PurchaseRecord.AcknowledgementDueAt"/> null). 기록이 없으면 아무것도 하지 않습니다.</summary>
		Task MarkAcknowledgedAsync(string store, string transactionId, CancellationToken cancellationToken);

		/// <summary>
		/// 확인하지 못한 기록의 다음 시도 시각을 <paramref name="dueAt"/> 으로 미룹니다. 이미 확인했거나 기록이 없으면 아무것도 하지 않습니다(확인을 다시 대기로 되돌리지 않음).
		/// </summary>
		Task DeferAcknowledgementAsync(string store, string transactionId, DateTimeOffset dueAt, CancellationToken cancellationToken);

		/// <summary>
		/// 한 트랜잭션에서 거래의 누적 환불 수량을 <see cref="RefundChange.RefundedQuantity"/>(0~수량으로 자름)로 맞춥니다.
		/// <list type="bullet">
		/// <item>기록이 없으면 계정 없는 지급 전 환불 기록(<see cref="RefundChange.ProductId"/>, <see cref="RefundChange.Quantity"/>, 환불 수량·시각)을 넣고
		/// <see cref="LedgerRefundOutcome.RecordedBeforeGrant"/> 입니다. 지급 전 환불 기록을 고칠 때도 같은 결과이고, 둘 다 <paramref name="apply"/> 를 부르지 않습니다(회수할 지급이 없음).</item>
		/// <item><see cref="RefundUpdateMode.EventOrdered"/>: <see cref="RefundChange.ChangedAt"/> 이 기록의 <see cref="PurchaseRecord.RefundedAt"/> 보다 이르면 <see cref="LedgerRefundOutcome.Stale"/>.
		/// 수량이 같으면 <see cref="LedgerRefundOutcome.Unchanged"/> 이고 <see cref="PurchaseRecord.RefundedAt"/> 만 앞당깁니다(먼저 온 환불 취소 뒤에 늦게 온 환불을 거르려고).</item>
		/// <item><see cref="RefundUpdateMode.NeverDecrease"/>: 목표는 기록과 새 값 중 큰 값이고 시각으로 거르지 않습니다. <see cref="PurchaseRecord.RefundedAt"/> 은 둘 중 늦은 시각입니다.</item>
		/// </list>
		/// 지급 기록의 수량이 바뀌면 <paramref name="apply"/>(바꾸기 전 기록, 늘어난 수량 — 줄면 음수)를 같은 트랜잭션에서 부른 뒤(게임의 회수·복구 쓰기도 이 트랜잭션 안) 수량과 시각을 기록하고 커밋합니다.
		/// </summary>
		/// <remarks><paramref name="apply"/> 가 예외를 던지면 롤백하고 예외를 그대로 던집니다(알림은 다시 옴).</remarks>
		Task<LedgerRefundResult> UpdateRefundAsync(RefundChange change, Func<PurchaseRecord, int, CancellationToken, Task> apply, CancellationToken cancellationToken);

		/// <summary>
		/// 구독의 원래 거래 ID(App Store <c>originalTransactionId</c>)로 지급한 기록을 찾습니다. 지급 기록 중 <see cref="PurchaseRecord.OriginalTransactionId"/> 나 <see cref="PurchaseRecord.TransactionId"/> 가
		/// 그 값인 가장 최근 것이고(지급 전 환불 기록은 빼고), 없으면 null 입니다. 서버 알림이 구독의 계정을 찾을 때 씁니다(처음 거래가 아닌 갱신 거래만 지급한 경우 포함, DB 는 이 열에 색인).
		/// </summary>
		Task<PurchaseRecord> FindByOriginalTransactionAsync(string store, string originalTransactionId, CancellationToken cancellationToken);

		/// <summary>
		/// 구독 상태를 구독마다 한 번에 하나씩, 관측 시각 순서로 반영합니다. 한 트랜잭션(그 구독 행 잠금)에서 저장된 마지막 상태(없으면 null)로 <paramref name="next"/> 를 부르고
		/// (Google 은 이 안에서 지금 상태를 조회), 돌려받은 상태가 null 이면 <see cref="SubscriptionUpdateOutcome.Skipped"/>, 그 <see cref="SubscriptionUpdate.ObservedAt"/> 이 저장된 것보다 이르면
		/// <see cref="SubscriptionUpdateOutcome.Stale"/>, 아니면 <paramref name="deliver"/>(알림 처리기)를 같은 트랜잭션에서 부른 뒤 그 상태를 저장하고 <see cref="SubscriptionUpdateOutcome.Delivered"/> 입니다.
		/// </summary>
		/// <remarks>
		/// 여러 서버에서도 같은 구독의 조회·전달이 겹치지 않아야 하므로 잠금은 DB 에 둡니다(조회가 잠금 안에서 일어나 늦게 끝난 옛 조회가 새 상태를 덮지 못함).
		/// <paramref name="next"/>·<paramref name="deliver"/> 가 예외를 던지면 롤백하고 예외를 그대로 던집니다.
		/// </remarks>
		Task<SubscriptionUpdateOutcome> UpdateSubscriptionAsync(string store, string subscriptionId,
			Func<SubscriptionUpdate, CancellationToken, Task<SubscriptionUpdate>> next, Func<SubscriptionUpdate, CancellationToken, Task> deliver,
			CancellationToken cancellationToken);
	}
}
