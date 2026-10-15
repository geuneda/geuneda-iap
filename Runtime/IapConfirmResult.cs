// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary><see cref="IIapStore.ConfirmAsync"/> 의 결과 종류입니다.</summary>
	public enum IapConfirmStatus
	{
		/// <summary>스토어가 거래를 닫았습니다(소비·승인·완료)</summary>
		Confirmed = 1,

		/// <summary>이미 닫힌 거래입니다(다른 기기나 서버가 먼저 승인함). 다시 확정할 필요가 없습니다</summary>
		AlreadyConfirmed = 2,

		/// <summary>확정하지 못했습니다. 거래는 열린 채로 남고 서비스가 다시 시도합니다</summary>
		Failed = 3,
	}

	/// <summary><see cref="IIapStore.ConfirmAsync"/> 의 결과입니다. <c>default</c> 는 실패입니다.</summary>
	public readonly struct IapConfirmResult
	{
		private IapConfirmResult(IapConfirmStatus status, IapStoreFailureReason reason, string message)
		{
			Status = status;
			Reason = reason;
			Message = message;
		}

		public IapConfirmStatus Status { get; }

		/// <summary>실패 이유. <see cref="IapConfirmStatus.Failed"/> 일 때만 의미가 있습니다</summary>
		public IapStoreFailureReason Reason { get; }

		public string Message { get; }

		public static IapConfirmResult Confirmed()
		{
			return new IapConfirmResult(IapConfirmStatus.Confirmed, IapStoreFailureReason.Unknown, null);
		}

		public static IapConfirmResult AlreadyConfirmed(string message = null)
		{
			return new IapConfirmResult(IapConfirmStatus.AlreadyConfirmed, IapStoreFailureReason.DuplicateTransaction, message);
		}

		public static IapConfirmResult Failed(IapStoreFailureReason reason, string message = null)
		{
			return new IapConfirmResult(IapConfirmStatus.Failed, reason, message);
		}
	}
}
