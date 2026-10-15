using System.Threading;
using Cysharp.Threading.Tasks;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary>
	/// 게임이 구현하는 지급자입니다. 영수증을 게임 서버에 보내 검증하고 지급한 뒤 결과를 돌려줍니다.
	/// 서비스는 새 구매, 이전 결제 복구, 중복 거래 뒤 재조회, 늦게 끝난 결제 모두에서 이것을 부르고,
	/// 결과가 <see cref="FulfillmentStatus.Granted"/> 나 <see cref="FulfillmentStatus.AlreadyGranted"/> 일 때만 스토어 거래를 닫습니다.
	/// </summary>
	/// <remarks>
	/// <para>
	/// 서버는 영수증(또는 거래 ID)을 유일 키로 저장하고 지급과 같은 트랜잭션에서 처리해야 합니다. 같은 영수증이 다시 오면 오류가 아니라
	/// <see cref="FulfillmentStatus.AlreadyGranted"/> 로 답하세요. 검증만 하고 지급을 클라이언트가 따로 저장하면, 그 사이에 앱이 죽었을 때
	/// 재시도가 '이미 쓴 영수증'으로 거절되어 지급이 영구히 누락됩니다.
	/// </para>
	/// <para>
	/// 같은 주문이 시간 초과 뒤 다시 들어올 수 있습니다(앞 호출이 서버에서 끝났을 수도 있음). 서버가 멱등이면 안전합니다.
	/// </para>
	/// <para>
	/// 예외는 잡아서 Error 로그를 남기고 <see cref="FulfillmentStatus.RetryLater"/> 처럼 다룹니다(거래는 열린 채로 남음).
	/// 정상적인 실패는 예외 대신 결과로 돌려주세요.
	/// </para>
	/// </remarks>
	public interface IPurchaseFulfiller
	{
		/// <param name="order">지급할 주문</param>
		/// <param name="cancellationToken">시간 제한(<see cref="IapServiceOptions.FulfillTimeout"/>)이나 서비스 종료 때 취소됩니다</param>
		UniTask<FulfillmentResult> FulfillAsync(IapOrder order, CancellationToken cancellationToken);
	}
}
