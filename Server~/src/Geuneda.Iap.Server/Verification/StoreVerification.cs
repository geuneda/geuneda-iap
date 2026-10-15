using System;

namespace Geuneda.Iap.Server
{
	/// <summary>스토어 검증 결과의 종류입니다.</summary>
	public enum StoreVerificationStatus
	{
		/// <summary>지급할 수 있는 결제</summary>
		Verified = 1,

		/// <summary>서명·형식 오류이거나 스토어가 모르는 거래(<see cref="IapRedeemCodes.InvalidReceipt"/>)</summary>
		Invalid = 2,

		/// <summary>다른 앱의 거래(<see cref="IapRedeemCodes.AppMismatch"/>)</summary>
		AppMismatch = 3,

		/// <summary>거래의 스토어 상품이 요청한 상품과 다름(<see cref="IapRedeemCodes.ProductMismatch"/>)</summary>
		ProductMismatch = 4,

		/// <summary>허용하지 않은 환경(<see cref="IapRedeemCodes.EnvironmentNotAllowed"/>)</summary>
		EnvironmentNotAllowed = 5,

		/// <summary>환불·취소된 거래. 지급하지 않고 거래를 닫습니다(<see cref="IapRedeemCodes.Revoked"/>)</summary>
		Revoked = 6,

		/// <summary>결제가 아직 끝나지 않음(<see cref="IapRedeemCodes.PaymentPending"/>)</summary>
		Pending = 7,

		/// <summary>지금은 스토어에 확인할 수 없음. 클라이언트가 다시 보냅니다(<see cref="IapRedeemCodes.StoreUnavailable"/>)</summary>
		Unavailable = 8,
	}

	/// <summary>검증기(<see cref="IStoreVerifier"/>)의 결과입니다.</summary>
	public sealed class StoreVerification
	{
		private StoreVerification(StoreVerificationStatus status, VerifiedPurchase purchase, string message)
		{
			Status = status;
			Purchase = purchase;
			Message = message;
		}

		public StoreVerificationStatus Status { get; }

		/// <summary>확인한 결제. <see cref="StoreVerificationStatus.Verified"/> 가 아니면 null</summary>
		public VerifiedPurchase Purchase { get; }

		/// <summary>로그·응답용 설명. 성공이면 null</summary>
		public string Message { get; }

		public static StoreVerification Verified(VerifiedPurchase purchase)
		{
			return new StoreVerification(StoreVerificationStatus.Verified, purchase ?? throw new ArgumentNullException(nameof(purchase)), null);
		}

		/// <exception cref="ArgumentException"><paramref name="status"/> 가 <see cref="StoreVerificationStatus.Verified"/> 이거나 정의되지 않은 값</exception>
		public static StoreVerification Failed(StoreVerificationStatus status, string message)
		{
			if (status == StoreVerificationStatus.Verified || !Enum.IsDefined(status))
			{
				throw new ArgumentException($"Not a failure status: {status}", nameof(status));
			}

			return new StoreVerification(status, null, message ?? status.ToString());
		}

		public override string ToString()
		{
			return Message == null ? Status.ToString() : $"{Status}: {Message}";
		}
	}
}
