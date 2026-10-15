using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 게임이 구현하는 지급자입니다. 스토어가 확인한 결제의 보상을 계정에 넣습니다.
	/// 원장(<see cref="IPurchaseLedger"/>)의 트랜잭션 안에서 불리므로, 게임 데이터 쓰기는 그 트랜잭션에 참여해야 합니다(같은 DB 연결·세션).
	/// </summary>
	/// <remarks>
	/// <para>같은 거래로는 한 번만 성공합니다. 원장이 이미 기록한 거래면 부르지 않습니다.</para>
	/// <para>
	/// <see cref="GrantResult.Rejected"/>·<see cref="GrantResult.RetryLater"/> 를 돌려주거나 예외를 던지면 원장에 기록하지 않습니다(트랜잭션 롤백).
	/// 그러므로 실패를 돌려주기 전에 쓴 게임 데이터도 트랜잭션에 있어야 함께 되돌려집니다. 예외는 잡지 않아 HTTP 500 이 되고 클라이언트가 다시 보냅니다.
	/// </para>
	/// </remarks>
	public interface IPurchaseGrantor
	{
		Task<GrantResult> GrantAsync(PurchaseGrant grant, CancellationToken cancellationToken);
	}
}
