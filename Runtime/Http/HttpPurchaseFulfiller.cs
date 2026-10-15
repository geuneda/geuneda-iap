using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Http
{
	/// <summary>
	/// 결제 서버(<c>Geuneda.Iap.Server</c> 의 <c>MapGeunedaIap</c>)에 주문을 보내는 지급자입니다. <c>POST {기본 주소}purchases</c> 로 요청을 보내고 응답을 지급 결과로 바꿉니다.
	/// 결과의 <see cref="FulfillmentResult.Response"/> 는 <see cref="IapRedeemResponse{TGrant}"/> 입니다.
	/// </summary>
	/// <remarks>
	/// 계정 인증은 <paramref name="http"/> 의 미들웨어(account 패키지의 <c>AuthTokenMiddleware</c>)가 붙입니다. 로그인 전이라 401 이면 다시 시도로 남고,
	/// 로그인 뒤 <c>RecoverAsync</c> 로 다시 지급합니다. 연결 실패·시간 초과·5xx·해석할 수 없는 응답도 거래를 열어 두는 <see cref="FulfillmentResult.RetryLater"/> 입니다.
	/// </remarks>
	/// <typeparam name="TGrant">게임 서버 지급 결과(<see cref="IapRedeemResponse{TGrant}.Grant"/>)의 형식</typeparam>
	public sealed class HttpPurchaseFulfiller<TGrant> : IPurchaseFulfiller
	{
		private readonly IHttpClient _http;
		private readonly string _route;

		// 서버(System.Text.Json Web 기본값)와 같은 camelCase. 설정 객체는 바꿀 수 있으므로 지급자마다 따로 둔다
		private readonly JsonSerializerSettings _json = new JsonSerializerSettings
		{
			ContractResolver = new CamelCasePropertyNamesContractResolver(),
			NullValueHandling = NullValueHandling.Ignore
		};

		/// <param name="http">서버 접두사까지 담은 기본 주소(예: <c>https://game.example.com/iap/</c>)의 클라이언트</param>
		/// <param name="route">지급 경로. 기본 <see cref="IapRoutes.Purchases"/></param>
		public HttpPurchaseFulfiller(IHttpClient http, string route = IapRoutes.Purchases)
		{
			if (string.IsNullOrWhiteSpace(route))
			{
				throw new ArgumentException("Route is empty", nameof(route));
			}

			_http = http ?? throw new ArgumentNullException(nameof(http));
			_route = route;
		}

		public async UniTask<FulfillmentResult> FulfillAsync(IapOrder order, CancellationToken cancellationToken)
		{
			var request = HttpRequest.Post(_route).WithJsonBody(order.ToRedeemRequest(), _json);
			var sent = await _http.SendAsync(request, cancellationToken);

			if (!sent.TryGetValue(out var response))
			{
				return FulfillmentResult.RetryLater($"Request failed with code {sent.Code}: {sent.Message}");
			}

			if (response.StatusCode >= 500)
			{
				return FulfillmentResult.RetryLater($"HTTP {response.StatusCode}");
			}

			// 200 과 서버가 만든 400·401 은 지급 응답 본문을 담는다. 그 밖(프록시 오류 등)은 본문을 읽지 못해 다시 시도로 남는다
			var body = response.ReadJson<IapRedeemResponse<TGrant>>(_json);

			if (!body.TryGetValue(out var redeem) || redeem == null)
			{
				return FulfillmentResult.RetryLater($"Unreadable redeem response, HTTP {response.StatusCode}");
			}

			return redeem.ToFulfillmentResult();
		}
	}
}
