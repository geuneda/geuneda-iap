using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	internal enum GoogleApiStatus
	{
		Ok = 1,

		/// <summary>400·404·410: 그 구매 토큰·주문이 없음</summary>
		NotFound = 2,

		/// <summary>네트워크, 시간 초과, 할당량, 서버 장애, 자격 증명·권한, 해석할 수 없는 응답</summary>
		Unavailable = 3,
	}

	internal readonly struct GoogleApiResult<T>
	{
		public GoogleApiResult(GoogleApiStatus status, T value, string message)
		{
			Status = status;
			Value = value;
			Message = message;
		}

		public GoogleApiStatus Status { get; }
		public T Value { get; }
		public string Message { get; }
	}

	/// <summary>
	/// Android Publisher API v3 호출입니다. 실패는 예외 대신 <see cref="GoogleApiStatus"/> 이고, 호출자 취소만 예외입니다.
	/// 자격 증명·권한 오류(401, 403)는 설정 문제라 Error 로그를 남깁니다.
	/// </summary>
	internal sealed class GooglePlayApi
	{
		public const string HttpClientName = "Geuneda.Iap.GooglePlay";

		private const int MaxErrorBodyChars = 512;

		private readonly IHttpClientFactory _clients;
		private readonly IGooglePlayAccessTokenSource _tokens;
		private readonly string _package;
		private readonly ILogger<GooglePlayApi> _logger;

		public GooglePlayApi(IHttpClientFactory clients, IGooglePlayAccessTokenSource tokens, IOptions<GooglePlayOptions> options, ILogger<GooglePlayApi> logger)
		{
			_clients = clients;
			_tokens = tokens;
			_package = Uri.EscapeDataString(options.Value.PackageName);
			_logger = logger;
		}

		public Task<GoogleApiResult<GoogleProductPurchase>> GetProductAsync(string purchaseToken, CancellationToken cancellationToken)
		{
			return SendAsync<GoogleProductPurchase>(HttpMethod.Get, $"applications/{_package}/purchases/productsv2/tokens/{Uri.EscapeDataString(purchaseToken)}",
				cancellationToken);
		}

		public Task<GoogleApiResult<GoogleSubscriptionPurchase>> GetSubscriptionAsync(string purchaseToken, CancellationToken cancellationToken)
		{
			return SendAsync<GoogleSubscriptionPurchase>(HttpMethod.Get,
				$"applications/{_package}/purchases/subscriptionsv2/tokens/{Uri.EscapeDataString(purchaseToken)}", cancellationToken);
		}

		public Task<GoogleApiResult<GoogleOrder>> GetOrderAsync(string orderId, CancellationToken cancellationToken)
		{
			return SendAsync<GoogleOrder>(HttpMethod.Get, $"applications/{_package}/orders/{Uri.EscapeDataString(orderId)}", cancellationToken);
		}

		/// <summary>일회성 상품은 <c>purchases.products.acknowledge</c>, 구독은 <c>purchases.subscriptions.acknowledge</c> 입니다.</summary>
		public Task<GoogleApiResult<object>> AcknowledgeAsync(bool subscription, string productId, string purchaseToken, CancellationToken cancellationToken)
		{
			var kind = subscription ? "subscriptions" : "products";
			return SendAsync<object>(HttpMethod.Post,
				$"applications/{_package}/purchases/{kind}/{Uri.EscapeDataString(productId)}/tokens/{Uri.EscapeDataString(purchaseToken)}:acknowledge",
				cancellationToken);
		}

		private async Task<GoogleApiResult<T>> SendAsync<T>(HttpMethod method, string path, CancellationToken cancellationToken) where T : class
		{
			string accessToken;

			try
			{
				accessToken = await _tokens.GetAccessTokenAsync(cancellationToken);
			}
			catch (TokenResponseException e)
			{
				_logger.LogError(e, "Google Play access token was refused; check the service account key");
				return Unavailable<T>("Google access token was refused");
			}
			catch (HttpRequestException e)
			{
				_logger.LogWarning(e, "Could not reach Google to get an access token");
				return Unavailable<T>("Could not reach Google OAuth");
			}
			catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				return Unavailable<T>("Google OAuth timed out");
			}

			using (var request = new HttpRequestMessage(method, path))
			{
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

				if (method == HttpMethod.Post)
				{
					request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
				}

				HttpResponseMessage response;

				try
				{
					response = await _clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
				}
				catch (HttpRequestException e)
				{
					_logger.LogWarning(e, "Google Play request {Method} {Path} failed", method, path);
					return Unavailable<T>("Could not reach Google Play");
				}
				catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					_logger.LogWarning("Google Play request {Method} {Path} timed out", method, path);
					return Unavailable<T>("Google Play timed out");
				}

				using (response)
				{
					if (response.IsSuccessStatusCode)
					{
						return await ReadAsync<T>(response, method == HttpMethod.Post, path, cancellationToken);
					}

					var body = await ReadErrorAsync(response, cancellationToken);
					var message = $"Google Play {(int) response.StatusCode}: {body}";

					switch (response.StatusCode)
					{
						case HttpStatusCode.BadRequest:
						case HttpStatusCode.NotFound:
						case HttpStatusCode.Gone:
							return new GoogleApiResult<T>(GoogleApiStatus.NotFound, null, message);
						case HttpStatusCode.Unauthorized:
						case HttpStatusCode.Forbidden:
							_logger.LogError("Google Play refused {Method} {Path}; check the service account permissions in Play Console: {Message}", method, path, message);
							return Unavailable<T>(message);
						default:
							_logger.LogWarning("Google Play {Method} {Path} failed: {Message}", method, path, message);
							return Unavailable<T>(message);
					}
				}
			}
		}

		private async Task<GoogleApiResult<T>> ReadAsync<T>(HttpResponseMessage response, bool ignoreBody, string path, CancellationToken cancellationToken)
			where T : class
		{
			if (ignoreBody)
			{
				return new GoogleApiResult<T>(GoogleApiStatus.Ok, null, null);
			}

			T value;

			try
			{
				value = JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(cancellationToken), IapServerJson.Options);
			}
			catch (JsonException e)
			{
				_logger.LogError(e, "Google Play {Path} returned unreadable JSON", path);
				return Unavailable<T>("Unreadable Google Play response");
			}

			if (value == null)
			{
				_logger.LogError("Google Play {Path} returned an empty body", path);
				return Unavailable<T>("Empty Google Play response");
			}

			return new GoogleApiResult<T>(GoogleApiStatus.Ok, value, null);
		}

		private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
		{
			var body = await response.Content.ReadAsStringAsync(cancellationToken);
			return body.Length > MaxErrorBodyChars ? body.Substring(0, MaxErrorBodyChars) : body;
		}

		private static GoogleApiResult<T> Unavailable<T>(string message)
		{
			return new GoogleApiResult<T>(GoogleApiStatus.Unavailable, default, message);
		}
	}
}
