// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary><see cref="IIapService.RecoverAsync"/> 가 처리한 미확정 주문의 결과 수입니다.</summary>
	public readonly struct RecoverySummary
	{
		internal RecoverySummary(int granted, int retrying, int rejected, int unresolved)
		{
			Granted = granted;
			Retrying = retrying;
			Rejected = rejected;
			Unresolved = unresolved;
		}

		/// <summary>지급까지 끝난 주문(확정만 실패한 것 포함)</summary>
		public int Granted { get; }

		/// <summary>지급자가 나중에를 돌려줬거나 시간 초과·예외로 다시 시도할 주문</summary>
		public int Retrying { get; }

		public int Rejected { get; }

		/// <summary>스토어나 정의가 상품을 몰라 지급·확정하지 않은 주문</summary>
		public int Unresolved { get; }

		public int Total => Granted + Retrying + Rejected + Unresolved;

		public override string ToString()
		{
			return $"granted {Granted}, retrying {Retrying}, rejected {Rejected}, unresolved {Unresolved}";
		}
	}
}
