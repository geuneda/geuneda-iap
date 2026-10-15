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
		private static RefundChange Change(string transactionId, int refunded, DateTimeOffset at, RefundUpdateMode mode, int quantity = 1)
		{
			return new RefundChange
			{
				Store = IapStoreNames.Fake, TransactionId = transactionId, RefundedQuantity = refunded, ChangedAt = at, Mode = mode, ProductId = "gem_100",
				Quantity = quantity
			};
		}

		private static PurchaseRecord Record(string transactionId = "tx-1", string account = "account-1")
		{
			return new PurchaseRecord { Store = IapStoreNames.Fake, TransactionId = transactionId, AccountId = account, ProductId = "gem_100" };
		}

		[Fact]
		public async Task Records_with_the_grant_response_and_skips_duplicates()
		{
			var ledger = new InMemoryPurchaseLedger();
			var calls = 0;

			var first = await ledger.RedeemAsync(Record(), (_, _) =>
			{
				calls++;
				return Task.FromResult(LedgerGrant.Granted("{\"gems\":1}"));
			}, CancellationToken.None);
			var second = await ledger.RedeemAsync(Record(account: "account-2"), (_, _) =>
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

			var notGranted = await ledger.RedeemAsync(Record(), (_, _) => Task.FromResult(LedgerGrant.NotGranted), CancellationToken.None);
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				ledger.RedeemAsync(Record(), (_, _) => throw new InvalidOperationException("bug"), CancellationToken.None));

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
				await ledger.RedeemAsync(record, (_, _) => Task.FromResult(LedgerGrant.Granted(null)), CancellationToken.None);
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
		public async Task Refund_quantity_changes_once_in_event_order()
		{
			var ledger = new InMemoryPurchaseLedger();
			var at = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
			await ledger.RedeemAsync(Record() with { Quantity = 3 }, (_, _) => Task.FromResult(LedgerGrant.Granted(null)), CancellationToken.None);
			var deltas = new System.Collections.Generic.List<int>();
			Task Apply(PurchaseRecord record, int delta, CancellationToken token)
			{
				deltas.Add(delta);
				return Task.CompletedTask;
			}

			const RefundUpdateMode ordered = RefundUpdateMode.EventOrdered;
			var missing = await ledger.UpdateRefundAsync(Change("missing", 1, at, ordered), Apply, CancellationToken.None);
			var first = await ledger.UpdateRefundAsync(Change("tx-1", 1, at, ordered), Apply, CancellationToken.None);
			var same = await ledger.UpdateRefundAsync(Change("tx-1", 1, at.AddMinutes(1), ordered), Apply, CancellationToken.None);
			var clamped = await ledger.UpdateRefundAsync(Change("tx-1", 9, at.AddMinutes(2), ordered), Apply, CancellationToken.None);
			var stale = await ledger.UpdateRefundAsync(Change("tx-1", 0, at.AddMinutes(1), ordered), Apply, CancellationToken.None);
			await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.UpdateRefundAsync(Change("tx-1", 0, at.AddMinutes(3), ordered),
				(_, _, _) => throw new InvalidOperationException("bug"), CancellationToken.None));
			var reversed = await ledger.UpdateRefundAsync(Change("tx-1", 0, at.AddMinutes(4), ordered), Apply, CancellationToken.None);

			Assert.Equal(LedgerRefundOutcome.RecordedBeforeGrant, missing.Outcome);
			Assert.False(missing.Record.IsGranted);
			Assert.Equal(LedgerRefundOutcome.Changed, first.Outcome);
			Assert.Equal(LedgerRefundOutcome.Unchanged, same.Outcome);
			Assert.Equal(3, clamped.Record.RefundedQuantity);
			Assert.True(clamped.Record.IsFullyRefunded);
			Assert.Equal(LedgerRefundOutcome.Stale, stale.Outcome);
			Assert.Equal(LedgerRefundOutcome.Changed, reversed.Outcome);
			Assert.Equal(new[] { 1, 2, -3 }, deltas);
			Assert.Equal(at.AddMinutes(4), (await ledger.FindAsync(IapStoreNames.Fake, "tx-1", CancellationToken.None)).RefundedAt);
		}

		[Fact]
		public async Task Unchanged_event_advances_the_time_so_an_older_refund_is_stale()
		{
			// 환불 취소(t2)가 먼저 오고 그 전의 환불(t1)이 늦게 오면, 최종 상태는 취소다(리뷰 2b 1회차)
			var ledger = new InMemoryPurchaseLedger();
			var at = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
			await ledger.RedeemAsync(Record(), (_, _) => Task.FromResult(LedgerGrant.Granted(null)), CancellationToken.None);
			var calls = 0;

			var reversal = await ledger.UpdateRefundAsync(Change("tx-1", 0, at.AddMinutes(2), RefundUpdateMode.EventOrdered),
				(_, _, _) => Task.FromResult(++calls), CancellationToken.None);
			var refund = await ledger.UpdateRefundAsync(Change("tx-1", 1, at.AddMinutes(1), RefundUpdateMode.EventOrdered),
				(_, _, _) => Task.FromResult(++calls), CancellationToken.None);

			Assert.Equal(LedgerRefundOutcome.Unchanged, reversal.Outcome);
			Assert.Equal(at.AddMinutes(2), reversal.Record.RefundedAt);
			Assert.Equal(LedgerRefundOutcome.Stale, refund.Outcome);
			Assert.Equal(0, calls);
		}

		[Fact]
		public async Task Never_decrease_ignores_time_and_lower_snapshots()
		{
			var ledger = new InMemoryPurchaseLedger();
			var at = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
			await ledger.RedeemAsync(Record() with { Quantity = 3 }, (_, _) => Task.FromResult(LedgerGrant.Granted(null)), CancellationToken.None);
			var deltas = new System.Collections.Generic.List<int>();

			Task Apply(PurchaseRecord record, int delta, CancellationToken token)
			{
				deltas.Add(delta);
				return Task.CompletedTask;
			}

			await ledger.UpdateRefundAsync(Change("tx-1", 2, at.AddMinutes(5), RefundUpdateMode.NeverDecrease), Apply, CancellationToken.None);
			var lower = await ledger.UpdateRefundAsync(Change("tx-1", 1, at.AddMinutes(9), RefundUpdateMode.NeverDecrease), Apply, CancellationToken.None);
			var olderButHigher = await ledger.UpdateRefundAsync(Change("tx-1", 3, at.AddMinutes(1), RefundUpdateMode.NeverDecrease), Apply,
				CancellationToken.None);

			Assert.Equal(LedgerRefundOutcome.Unchanged, lower.Outcome);
			Assert.Equal(LedgerRefundOutcome.Changed, olderButHigher.Outcome);
			Assert.Equal(new[] { 2, 1 }, deltas);
			Assert.Equal(at.AddMinutes(9), olderButHigher.Record.RefundedAt);
		}

		[Fact]
		public async Task Original_transaction_finds_the_latest_grant_of_the_subscription()
		{
			var ledger = new InMemoryPurchaseLedger();
			var at = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
			await ledger.RedeemAsync(Record("t1", "account-1") with { OriginalTransactionId = "t0", RedeemedAt = at },
				(_, _) => Task.FromResult(LedgerGrant.Granted(null)), CancellationToken.None);
			await ledger.RedeemAsync(Record("t2", "account-2") with { OriginalTransactionId = "t0", RedeemedAt = at.AddDays(30) },
				(_, _) => Task.FromResult(LedgerGrant.Granted(null)), CancellationToken.None);

			Assert.Equal("account-2", (await ledger.FindByOriginalTransactionAsync(IapStoreNames.Fake, "t0", CancellationToken.None)).AccountId);
			Assert.Equal("account-1", (await ledger.FindByOriginalTransactionAsync(IapStoreNames.Fake, "t1", CancellationToken.None)).AccountId);
			Assert.Null(await ledger.FindByOriginalTransactionAsync(IapStoreNames.Fake, "t9", CancellationToken.None));
		}

		[Fact]
		public async Task Refund_before_grant_is_kept_and_carried_into_the_grant()
		{
			var ledger = new InMemoryPurchaseLedger();
			var at = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
			var calls = 0;

			var early = await ledger.UpdateRefundAsync(Change("tx-1", 2, at, RefundUpdateMode.NeverDecrease, quantity: 3), (_, _, _) => Task.FromResult(++calls),
				CancellationToken.None);
			PurchaseRecord seen = null;
			var granted = await ledger.RedeemAsync(Record() with { Quantity = 3 }, (preGrant, _) =>
			{
				seen = preGrant;
				return Task.FromResult(LedgerGrant.Granted(null));
			}, CancellationToken.None);
			var later = await ledger.UpdateRefundAsync(Change("tx-1", 3, at.AddMinutes(1), RefundUpdateMode.NeverDecrease), (_, delta, _) =>
			{
				calls += delta * 10;
				return Task.CompletedTask;
			}, CancellationToken.None);

			Assert.Equal(LedgerRefundOutcome.RecordedBeforeGrant, early.Outcome);
			Assert.Equal(2, seen.RefundedQuantity);
			Assert.Equal(LedgerRedeemOutcome.Recorded, granted.Outcome);
			Assert.True(granted.Record.IsGranted);
			Assert.Equal(2, granted.Record.RefundedQuantity);
			Assert.Equal(LedgerRefundOutcome.Changed, later.Outcome);
			Assert.Equal(10, calls);
			Assert.Null(await ledger.FindByOriginalTransactionAsync(IapStoreNames.Fake, "missing", CancellationToken.None));
		}

		[Fact]
		public async Task Subscription_updates_are_delivered_in_observation_order()
		{
			var ledger = new InMemoryPurchaseLedger();
			var at = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
			var delivered = new System.Collections.Generic.List<IapSubscriptionState>();
			SubscriptionUpdate Update(IapSubscriptionState state, DateTimeOffset observedAt) => new SubscriptionUpdate
				{ Store = IapStoreNames.Fake, SubscriptionId = "sub-1", State = state, ObservedAt = observedAt };
			Task Deliver(SubscriptionUpdate update, CancellationToken token)
			{
				delivered.Add(update.State);
				return Task.CompletedTask;
			}

			var first = await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (_, _) => Task.FromResult(Update(IapSubscriptionState.Active, at.AddMinutes(2))),
				Deliver, CancellationToken.None);
			var stale = await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (_, _) => Task.FromResult(Update(IapSubscriptionState.Expired, at)), Deliver,
				CancellationToken.None);
			SubscriptionUpdate passed = null;
			var skipped = await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (stored, _) =>
			{
				passed = stored;
				return Task.FromResult<SubscriptionUpdate>(null);
			}, Deliver, CancellationToken.None);

			Assert.Equal(SubscriptionUpdateOutcome.Delivered, first);
			Assert.Equal(SubscriptionUpdateOutcome.Stale, stale);
			Assert.Equal(SubscriptionUpdateOutcome.Skipped, skipped);
			Assert.Equal(IapSubscriptionState.Active, passed.State);
			Assert.Equal(new[] { IapSubscriptionState.Active }, delivered);
		}

		[Fact]
		public async Task Different_transactions_do_not_wait_for_each_other()
		{
			var ledger = new InMemoryPurchaseLedger();
			var release = new TaskCompletionSource<LedgerGrant>(TaskCreationOptions.RunContinuationsAsynchronously);

			var blocked = ledger.RedeemAsync(Record("tx-1"), (_, _) => release.Task, CancellationToken.None);
			var other = await ledger.RedeemAsync(Record("tx-2"), (_, _) => Task.FromResult(LedgerGrant.Granted(null)), CancellationToken.None)
				.WaitAsync(TimeSpan.FromSeconds(5));
			release.SetResult(LedgerGrant.Granted(null));

			Assert.Equal(LedgerRedeemOutcome.Recorded, other.Outcome);
			Assert.Equal(LedgerRedeemOutcome.Recorded, (await blocked).Outcome);
		}
	}
}
