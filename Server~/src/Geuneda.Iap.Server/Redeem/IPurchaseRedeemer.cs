using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 지급 요청을 처리합니다. 원장에 지급 기록이 있으면 스토어를 부르지 않고 답하고, 없으면(지급 전 환불 기록뿐이어도) 스토어에 검증한 뒤 원장과 같은 트랜잭션에서 지급합니다.
	/// HTTP 엔드포인트(<see cref="IapServerExtensions.MapGeunedaIap"/>)가 쓰고, 실시간 RPC 처럼 다른 경로에서는 직접 부릅니다(요청 범위 서비스).
	/// </summary>
	public interface IPurchaseRedeemer
	{
		/// <param name="accountId">인증한 계정 ID. 비었으면 <see cref="System.ArgumentException"/></param>
		/// <param name="request">클라이언트 요청. null 이면 <see cref="System.ArgumentNullException"/></param>
		/// <param name="cancellationToken">요청이 끊기면 취소됩니다. 지급 전이면 롤백되고 클라이언트가 다시 보냅니다</param>
		/// <returns>
		/// 응답. <see cref="IapRedeemResponse{TGrant}.Grant"/> 는 이번 지급이면 지급자 응답 객체, 중복이면 원장에 저장한 JSON(<see cref="System.Text.Json.JsonElement"/>)입니다.
		/// 요청 필드가 비었으면 <see cref="IapRedeemCodes.BadRequest"/> 입니다
		/// </returns>
		Task<IapRedeemResponse<object>> RedeemAsync(string accountId, IapRedeemRequest request, CancellationToken cancellationToken);
	}
}
