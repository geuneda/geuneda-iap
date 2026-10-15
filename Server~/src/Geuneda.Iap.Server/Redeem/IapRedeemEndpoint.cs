using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// <c>POST {prefix}/purchases</c> 처리기입니다. 401(계정 확인 실패), 415(JSON 아님), 413(본문 초과), 400(형식 오류), 200(그 밖의 모든 결과, 본문의 <c>status</c> 로 구분)을 돌려줍니다.
	/// 지급자·원장 예외는 잡지 않아 500 이 되고 클라이언트가 다시 보냅니다.
	/// </summary>
	internal static class IapRedeemEndpoint
	{
		public static async Task<IResult> HandleAsync(HttpContext context, Func<HttpContext, Task<string>> resolveAccountId)
		{
			var accountId = await resolveAccountId(context);

			if (string.IsNullOrEmpty(accountId))
			{
				return Error(StatusCodes.Status401Unauthorized, IapRedeemStatus.RetryLater, IapRedeemCodes.Unauthorized, "Account is not authenticated");
			}

			if (!context.Request.HasJsonContentType())
			{
				return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
			}

			var maxBytes = context.RequestServices.GetRequiredService<IOptions<IapServerOptions>>().Value.MaxRequestBytes;
			var body = context.Request.ContentLength > maxBytes ? null : await ReadBodyAsync(context.Request.Body, maxBytes, context.RequestAborted);

			if (body == null)
			{
				return Error(StatusCodes.Status413PayloadTooLarge, IapRedeemStatus.Rejected, IapRedeemCodes.BadRequest, $"Body is larger than {maxBytes} bytes");
			}

			IapRedeemRequest request;

			try
			{
				request = JsonSerializer.Deserialize<IapRedeemRequest>(body, IapServerJson.Options);
			}
			catch (JsonException e)
			{
				return Error(StatusCodes.Status400BadRequest, IapRedeemStatus.Rejected, IapRedeemCodes.BadRequest, $"Invalid JSON: {e.Message}");
			}

			if (request == null)
			{
				return Error(StatusCodes.Status400BadRequest, IapRedeemStatus.Rejected, IapRedeemCodes.BadRequest, "Body is null");
			}

			var redeemer = context.RequestServices.GetRequiredService<IPurchaseRedeemer>();
			var response = await redeemer.RedeemAsync(accountId, request, context.RequestAborted);
			var status = response.Code == IapRedeemCodes.BadRequest ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK;
			return Results.Json(response, IapServerJson.Options, statusCode: status);
		}

		private static IResult Error(int httpStatus, IapRedeemStatus status, int code, string message)
		{
			return Results.Json(new IapRedeemResponse<object> { Status = status, Code = code, Message = message, Quantity = 1 }, IapServerJson.Options,
				statusCode: httpStatus);
		}

		/// <summary>본문을 읽습니다. <paramref name="maxBytes"/> 를 넘으면 null 입니다(Content-Length 없이 보낸 본문도 막음).</summary>
		private static async Task<byte[]> ReadBodyAsync(Stream body, int maxBytes, CancellationToken cancellationToken)
		{
			using (var buffer = new MemoryStream())
			{
				var chunk = new byte[16 * 1024];
				int read;

				while ((read = await body.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)) > 0)
				{
					if (buffer.Length + read > maxBytes)
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
