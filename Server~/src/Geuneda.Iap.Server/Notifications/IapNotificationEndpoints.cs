using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 서버 알림 엔드포인트 처리기입니다. 200(반영·할 일 없음), 503(다시 보내 달라), 401(검증 실패), 400(해석 실패), 413(본문 초과)을 돌려줍니다.
	/// 알림 처리기·원장 예외는 잡지 않아 500 이 되고 스토어가 다시 보냅니다(환불은 원장이 한 번만 반영).
	/// </summary>
	internal static class IapNotificationEndpoints
	{
		public const string LoggerCategory = "Geuneda.Iap.Server.Notifications";

		private const int MaxBodyBytes = 256 * 1024;
		private const string BearerPrefix = "Bearer ";

		/// <summary>Google Play 실시간 개발자 알림(Pub/Sub 푸시, OIDC 인증)</summary>
		public static async Task<IResult> HandleGooglePlayAsync(HttpContext context)
		{
			var services = context.RequestServices;
			var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
			var header = context.Request.Headers.Authorization.ToString();
			var token = header.StartsWith(BearerPrefix, StringComparison.Ordinal) ? header.Substring(BearerPrefix.Length) : null;
			var (status, message) = await services.GetRequiredService<GooglePubSubTokenVerifier>().VerifyAsync(token, context.RequestAborted);

			if (status != PubSubTokenStatus.Valid)
			{
				logger.LogWarning("Refused a Google Play notification push: {Message}", message);
				return Results.StatusCode(status == PubSubTokenStatus.Unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status401Unauthorized);
			}

			var body = await ReadBodyAsync(context);

			if (body == null)
			{
				return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
			}

			GoogleDeveloperNotification notification;

			try
			{
				var envelope = JsonSerializer.Deserialize<PubSubPushEnvelope>(body, IapServerJson.Options);
				var data = Convert.FromBase64String(envelope?.Message?.Data ?? string.Empty);
				notification = data.Length == 0 ? null : JsonSerializer.Deserialize<GoogleDeveloperNotification>(data, IapServerJson.Options);
			}
			catch (Exception e) when (e is JsonException || e is FormatException)
			{
				// 거절하면 Pub/Sub 이 보관 기간 동안 다시 보낸다. 해석 버그를 고친 뒤 놓친 알림을 받을 수 있다
				logger.LogError(e, "Could not read a Google Play notification push");
				return Results.StatusCode(StatusCodes.Status400BadRequest);
			}

			if (notification == null)
			{
				logger.LogError("Google Play notification push has no data");
				return Results.StatusCode(StatusCodes.Status400BadRequest);
			}

			var refunds = ActivatorUtilities.CreateInstance<RefundApplier>(services);
			var outcome = await ActivatorUtilities.CreateInstance<GooglePlayNotificationProcessor>(services, refunds).ProcessAsync(notification, context.RequestAborted);
			return ToResult(outcome);
		}

		/// <summary>App Store Server Notifications V2(<c>{"signedPayload": JWS}</c>)</summary>
		public static async Task<IResult> HandleAppStoreAsync(HttpContext context)
		{
			var services = context.RequestServices;
			var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
			var body = await ReadBodyAsync(context);

			if (body == null)
			{
				return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
			}

			AppleNotificationRequest request;

			try
			{
				request = JsonSerializer.Deserialize<AppleNotificationRequest>(body, IapServerJson.Options);
			}
			catch (JsonException e)
			{
				logger.LogError(e, "Could not read an App Store notification");
				return Results.StatusCode(StatusCodes.Status400BadRequest);
			}

			var refunds = ActivatorUtilities.CreateInstance<RefundApplier>(services);
			var outcome = await ActivatorUtilities.CreateInstance<AppStoreNotificationProcessor>(services, refunds)
				.ProcessAsync(request?.SignedPayload, context.RequestAborted);
			return ToResult(outcome);
		}

		private static IResult ToResult(NotificationOutcome outcome)
		{
			switch (outcome)
			{
				case NotificationOutcome.Processed:
				case NotificationOutcome.Ignored:
					return Results.Ok();
				case NotificationOutcome.RetryLater:
					return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
				case NotificationOutcome.Unauthorized:
					return Results.StatusCode(StatusCodes.Status401Unauthorized);
				case NotificationOutcome.BadRequest:
					return Results.StatusCode(StatusCodes.Status400BadRequest);
				default:
					throw new InvalidOperationException($"Undefined notification outcome {(int) outcome}");
			}
		}

		/// <summary>본문을 읽습니다. 너무 크면 null 입니다.</summary>
		private static async Task<byte[]> ReadBodyAsync(HttpContext context)
		{
			if (context.Request.ContentLength > MaxBodyBytes)
			{
				return null;
			}

			using (var buffer = new MemoryStream())
			{
				var chunk = new byte[16 * 1024];
				int read;

				while ((read = await context.Request.Body.ReadAsync(chunk.AsMemory(0, chunk.Length), context.RequestAborted)) > 0)
				{
					if (buffer.Length + read > MaxBodyBytes)
					{
						return null;
					}

					buffer.Write(chunk, 0, read);
				}

				return buffer.ToArray();
			}
		}
	}
}
