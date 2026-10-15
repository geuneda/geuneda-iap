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
