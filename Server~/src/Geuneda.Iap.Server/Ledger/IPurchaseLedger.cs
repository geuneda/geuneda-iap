using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 영수증 유일성 원장입니다. 거래 하나를 한 번만 지급하도록, 원장 기록과 게임 지급을 한 트랜잭션으로 묶습니다.
	/// 게임 DB 로 구현합니다(개발·테스트용 <see cref="InMemoryPurchaseLedger"/>).
	/// </summary>
	/// <remarks>
	/// <para>
	/// 검증만 하고 지급 저장을 따로 하면, 그 사이 서버나 앱이 죽었을 때 재시도가 '이미 쓴 영수증'으로 거절되어 지급이 영구히 누락됩니다.
	/// 반대로 원장 기록 없이 지급만 하면 같은 영수증으로 여러 번 지급됩니다.
	/// </para>
	/// <para>
	/// 구현 방법(관계형 DB 예): (Store, TransactionId) 에 유일 제약을 둔 표를 만들고, 한 트랜잭션에서 (1) 기록을 찾아 있으면 <see cref="LedgerRedeemResult.Duplicate"/>,
	/// (2) 없으면 <c>grant</c> 를 불러(지급자의 게임 데이터 쓰기도 이 트랜잭션 안), (3) 지급했으면 기록을 넣고 커밋합니다.
	/// 동시에 온 같은 거래가 (3) 에서 유일 제약에 걸리면 롤백하고(그 트랜잭션의 지급도 함께 되돌려짐) 이긴 기록을 다시 읽어 <see cref="LedgerRedeemResult.Duplicate"/> 로 돌려줍니다.
	/// 지급자가 같은 트랜잭션을 쓰게 하려면 요청 범위(scoped) DB 컨텍스트·세션을 원장과 지급자가 함께 받게 합니다.
	/// </para>
	/// </remarks>
	public interface IPurchaseLedger
	{
		/// <summary>기록을 찾습니다. 없으면 null 입니다. 트랜잭션 밖에서 먼저 확인할 때 씁니다(스토어 조회를 아끼려고).</summary>
		Task<PurchaseRecord> FindAsync(string store, string transactionId, CancellationToken cancellationToken);

		/// <summary>
		/// 한 트랜잭션에서 같은 키의 기록이 없을 때만 <paramref name="grant"/> 를 부르고, 지급했으면 <paramref name="record"/> 에 지급 응답(<see cref="LedgerGrant.Response"/>)을 넣어 기록합니다.
		/// </summary>
		/// <returns>
		/// 기록했으면 <see cref="LedgerRedeemResult.Recorded"/>(넣은 기록), 이미 있으면 <see cref="LedgerRedeemResult.Duplicate"/>(있던 기록),
		/// 지급하지 않았으면 <see cref="LedgerRedeemResult.NotGranted"/>(롤백)
		/// </returns>
		/// <remarks>
		/// 트랜잭션 안에서 기록을 찾았으면 <paramref name="grant"/> 를 부르지 않고 <see cref="LedgerRedeemResult.Duplicate"/> 입니다. 동시 요청과 경쟁해 커밋 때 유일 제약에 걸렸으면
		/// <paramref name="grant"/> 를 이미 불렀더라도 트랜잭션 전체(지급 포함)를 롤백하고 <see cref="LedgerRedeemResult.Duplicate"/> 입니다.
		/// <paramref name="grant"/> 가 예외를 던지면 롤백하고 예외를 그대로 던집니다.
		/// </remarks>
		Task<LedgerRedeemResult> RedeemAsync(PurchaseRecord record, Func<CancellationToken, Task<LedgerGrant>> grant, CancellationToken cancellationToken);

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
	}
}
