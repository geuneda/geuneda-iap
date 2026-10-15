using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>메모리 원장의 트랜잭션 규칙(중복이면 지급하지 않음, 지급 실패·예외면 기록 없음, 같은 거래는 하나씩)을 확인합니다.</summary>
	public sealed class LedgerTest
	{
		private static PurchaseRecord Record(string transactionId = "tx-1", string account = "account-1")
		{
			return new PurchaseRecord { Store = IapStoreNames.Fake, TransactionId = transactionId, AccountId = account, ProductId = "gem_100" };
		}

		[Fact]
		public async Task Records_with_the_grant_response_and_skips_duplicates()
		{
			var ledger = new InMemoryPurchaseLedger();
			var calls = 0;

			var first = await ledger.RedeemAsync(Record(), _ =>
			{
				calls++;
				return Task.FromResult(LedgerGrant.Granted("{\"gems\":1}"));
			}, CancellationToken.None);
			var second = await ledger.RedeemAsync(Record(account: "account-2"), _ =>
			{
				calls++;
				return Task.FromResult(LedgerGrant.Granted("{}"));
			}, CancellationToken.None);

			Assert.Equal(LedgerRedeemOutcome.Recorded, first.Outcome);
			Assert.Equal("{\"gems\":1}", first.Record.GrantResponse);
			Assert.Equal(LedgerRedeemOutcome.Duplicate, second.Outcome);
			Assert.Equal("account-1", second.Record.AccountId);
			Assert.Equal(1, calls);
		}

		[Fact]
		public async Task Not_granted_and_exceptions_leave_no_record()
		{
			var ledger = new InMemoryPurchaseLedger();

			var notGranted = await ledger.RedeemAsync(Record(), _ => Task.FromResult(LedgerGrant.NotGranted), CancellationToken.None);
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				ledger.RedeemAsync(Record(), _ => throw new InvalidOperationException("bug"), CancellationToken.None));

			Assert.Equal(LedgerRedeemOutcome.NotGranted, notGranted.Outcome);
			Assert.Null(notGranted.Record);
			Assert.Null(await ledger.FindAsync(IapStoreNames.Fake, "tx-1", CancellationToken.None));
		}

		[Fact]
		public async Task Due_acknowledgements_are_listed_by_due_time_until_marked_or_deferred()
		{
			var ledger = new InMemoryPurchaseLedger();
			var now = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

			foreach (var (id, due) in new[] { ("tx-2", (DateTimeOffset?) now.AddMinutes(-1)), ("tx-1", now.AddMinutes(-2)), ("tx-3", null), ("tx-4", now.AddMinutes(1)) })
			{
				var record = Record(id) with { AcknowledgementDueAt = due };
				await ledger.RedeemAsync(record, _ => Task.FromResult(LedgerGrant.Granted(null)), CancellationToken.None);
			}

			var listed = await ledger.FindDueAcknowledgementsAsync(now, 10, CancellationToken.None);
			await ledger.MarkAcknowledgedAsync(IapStoreNames.Fake, "tx-1", CancellationToken.None);
			await ledger.MarkAcknowledgedAsync(IapStoreNames.Fake, "missing", CancellationToken.None);
			await ledger.DeferAcknowledgementAsync(IapStoreNames.Fake, "tx-2", now.AddHours(1), CancellationToken.None);
			await ledger.DeferAcknowledgementAsync(IapStoreNames.Fake, "tx-1", now.AddHours(1), CancellationToken.None);
			var after = await ledger.FindDueAcknowledgementsAsync(now, 10, CancellationToken.None);
			var limited = await ledger.FindDueAcknowledgementsAsync(now.AddHours(2), 1, CancellationToken.None);

			Assert.Equal(new[] { "tx-1", "tx-2" }, listed.Select(record => record.TransactionId));
			Assert.Empty(after);
			Assert.Null((await ledger.FindAsync(IapStoreNames.Fake, "tx-1", CancellationToken.None)).AcknowledgementDueAt);
			Assert.Equal("tx-4", Assert.Single(limited).TransactionId);
		}

		[Fact]
		public async Task Different_transactions_do_not_wait_for_each_other()
		{
			var ledger = new InMemoryPurchaseLedger();
			var release = new TaskCompletionSource<LedgerGrant>(TaskCreationOptions.RunContinuationsAsynchronously);

			var blocked = ledger.RedeemAsync(Record("tx-1"), _ => release.Task, CancellationToken.None);
			var other = await ledger.RedeemAsync(Record("tx-2"), _ => Task.FromResult(LedgerGrant.Granted(null)), CancellationToken.None)
				.WaitAsync(TimeSpan.FromSeconds(5));
			release.SetResult(LedgerGrant.Granted(null));

			Assert.Equal(LedgerRedeemOutcome.Recorded, other.Outcome);
			Assert.Equal(LedgerRedeemOutcome.Recorded, (await blocked).Outcome);
		}
	}
}
