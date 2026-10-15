using System;

namespace Geuneda.Iap.Server
{
	/// <summary><see cref="IPurchaseLedger.RedeemAsync"/> 에 넘기는 지급 콜백의 결과입니다.</summary>
	public readonly struct LedgerGrant
	{
		private LedgerGrant(bool isGranted, string response)
		{
			IsGranted = isGranted;
			Response = response;
		}

		/// <summary>지급했으면 true(기록하고 커밋), 아니면 false(롤백)</summary>
		public bool IsGranted { get; }

		/// <summary>기록에 넣을 지급 응답 JSON(<see cref="PurchaseRecord.GrantResponse"/>). 없으면 null</summary>
		public string Response { get; }

		public static LedgerGrant Granted(string response)
		{
			return new LedgerGrant(true, response);
		}

		public static LedgerGrant NotGranted => new LedgerGrant(false, null);
	}

	/// <summary><see cref="IPurchaseLedger.UpdateRefundAsync"/> 가 알림의 순서를 정하는 방식입니다.</summary>
	public enum RefundUpdateMode
	{
		/// <summary>
		/// 스토어가 서명한 이벤트(App Store 환불·환불 취소)입니다. 이벤트 시각으로 순서를 정해, 기록된 마지막 반영보다 이른 이벤트는 <see cref="LedgerRefundOutcome.Stale"/> 이고
		/// 수량이 줄 수 있습니다(환불 취소). 수량이 같아도 더 늦은 이벤트면 반영 시각을 앞당깁니다.
		/// </summary>
		EventOrdered = 1,

		/// <summary>
		/// 서버가 조회한 지금 상태(Google 남은 환불 가능 수량)입니다. 조회 순서가 이벤트 순서와 다를 수 있어 시각으로 거르지 않고, 누적 환불 수량을 줄이지 않습니다
		/// (Google 은 환불을 취소하지 않음).
		/// </summary>
		NeverDecrease = 2,
	}

	/// <summary><see cref="IPurchaseLedger.UpdateRefundAsync"/> 결과의 종류입니다.</summary>
	public enum LedgerRefundOutcome
	{
		/// <summary>환불 수량을 바꿨고 콜백을 불렀습니다</summary>
		Changed = 1,

		/// <summary>환불 수량이 이미 같습니다(중복 알림, 줄일 수 없는 갱신). 콜백을 부르지 않습니다</summary>
		Unchanged = 2,

		/// <summary>기록된 마지막 환불 변경보다 오래된 이벤트입니다(순서가 뒤바뀐 알림)</summary>
		Stale = 3,

		/// <summary>원장에 없는 거래입니다(이 서버가 지급하지 않음)</summary>
		NotFound = 4,

		/// <summary>지급 전의 거래라 계정 없는 환불 기록으로 남겼습니다(나중의 지급 요청이 환불 수량만큼 빼고 지급하거나 닫음). 콜백을 부르지 않습니다</summary>
		RecordedBeforeGrant = 5,
	}

	/// <summary><see cref="IPurchaseLedger.UpdateSubscriptionAsync"/> 결과의 종류입니다.</summary>
	public enum SubscriptionUpdateOutcome
	{
		/// <summary>전달하고 저장했습니다</summary>
		Delivered = 1,

		/// <summary>바꿀 상태가 없었습니다(조회 실패, 저장된 것보다 오래된 알림 등)</summary>
		Skipped = 3,
	}

	/// <summary><see cref="IPurchaseLedger.UpdateRefundAsync"/> 의 결과입니다.</summary>
	public readonly struct LedgerRefundResult
	{
		private LedgerRefundResult(LedgerRefundOutcome outcome, PurchaseRecord record)
		{
			Outcome = outcome;
			Record = record;
		}

		public LedgerRefundOutcome Outcome { get; }

		/// <summary><see cref="LedgerRefundOutcome.Changed"/>·<see cref="LedgerRefundOutcome.RecordedBeforeGrant"/> 면 저장한 기록, <see cref="LedgerRefundOutcome.NotFound"/> 면 null, 그 밖은 있던 기록</summary>
		public PurchaseRecord Record { get; }

		public static LedgerRefundResult Changed(PurchaseRecord record)
		{
			return new LedgerRefundResult(LedgerRefundOutcome.Changed, record ?? throw new ArgumentNullException(nameof(record)));
		}

		public static LedgerRefundResult Unchanged(PurchaseRecord record)
		{
			return new LedgerRefundResult(LedgerRefundOutcome.Unchanged, record ?? throw new ArgumentNullException(nameof(record)));
		}

		public static LedgerRefundResult Stale(PurchaseRecord record)
		{
			return new LedgerRefundResult(LedgerRefundOutcome.Stale, record ?? throw new ArgumentNullException(nameof(record)));
		}

		public static LedgerRefundResult NotFound()
		{
			return new LedgerRefundResult(LedgerRefundOutcome.NotFound, null);
		}

		public static LedgerRefundResult RecordedBeforeGrant(PurchaseRecord record)
		{
			return new LedgerRefundResult(LedgerRefundOutcome.RecordedBeforeGrant, record ?? throw new ArgumentNullException(nameof(record)));
		}
	}

	/// <summary><see cref="IPurchaseLedger.RedeemAsync"/> 결과의 종류입니다.</summary>
	public enum LedgerRedeemOutcome
	{
		/// <summary>지급하고 기록했습니다</summary>
		Recorded = 1,

		/// <summary>이미 기록이 있어 지급하지 않았습니다</summary>
		Duplicate = 2,

		/// <summary>지급자가 지급하지 않아 기록하지 않았습니다</summary>
		NotGranted = 3,
	}

	/// <summary><see cref="IPurchaseLedger.RedeemAsync"/> 의 결과입니다.</summary>
	public readonly struct LedgerRedeemResult
	{
		private LedgerRedeemResult(LedgerRedeemOutcome outcome, PurchaseRecord record)
		{
			Outcome = outcome;
			Record = record;
		}

		public LedgerRedeemOutcome Outcome { get; }

		/// <summary><see cref="LedgerRedeemOutcome.Recorded"/> 면 넣은 기록, <see cref="LedgerRedeemOutcome.Duplicate"/> 면 있던 기록, 그 밖은 null</summary>
		public PurchaseRecord Record { get; }

		public static LedgerRedeemResult Recorded(PurchaseRecord record)
		{
			return new LedgerRedeemResult(LedgerRedeemOutcome.Recorded, record ?? throw new ArgumentNullException(nameof(record)));
		}

		public static LedgerRedeemResult Duplicate(PurchaseRecord existing)
		{
			return new LedgerRedeemResult(LedgerRedeemOutcome.Duplicate, existing ?? throw new ArgumentNullException(nameof(existing)));
		}

		public static LedgerRedeemResult NotGranted()
		{
			return new LedgerRedeemResult(LedgerRedeemOutcome.NotGranted, null);
		}
	}
}
