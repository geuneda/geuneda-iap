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
		public async Task Subscription_snapshots_are_what_next_returns()
		{
			// 순서와 합치기는 next 가 정한다. 원장은 저장된 것을 넘기고, null 이면 아무것도 하지 않고, 아니면 전달 뒤 저장한다(관측 시각을 비교하지 않음)
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
			var earlier = await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (_, _) => Task.FromResult(Update(IapSubscriptionState.Expired, at)), Deliver,
				CancellationToken.None);
			SubscriptionUpdate passed = null;
			var skipped = await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (stored, _) =>
			{
				passed = stored;
				return Task.FromResult<SubscriptionUpdate>(null);
			}, Deliver, CancellationToken.None);

			Assert.Equal(SubscriptionUpdateOutcome.Delivered, first);
			Assert.Equal(SubscriptionUpdateOutcome.Delivered, earlier);
			Assert.Equal(SubscriptionUpdateOutcome.Skipped, skipped);
			Assert.Equal(IapSubscriptionState.Expired, passed.State);
			Assert.Equal(new[] { IapSubscriptionState.Active, IapSubscriptionState.Expired }, delivered);
		}

		[Fact]
		public async Task Subscription_update_inside_a_grant_commits_with_the_grant()
		{
			// 구독 지급은 지급 콜백 안에서 구독을 갱신한다. 그 스냅숏은 지급을 기록할 때 저장되고, 그동안 같은 구독의 다른 갱신은 기다리며, 지급이 실패하면 버린다(리뷰 2b 8회차)
			var ledger = new InMemoryPurchaseLedger();
			var snapshot = new SubscriptionUpdate { Store = IapStoreNames.Fake, SubscriptionId = "sub-1", AccountId = "account-1", State = IapSubscriptionState.Active };
			Task Deliver(SubscriptionUpdate update, CancellationToken token) => Task.CompletedTask;
			var updated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

			var failed = ledger.RedeemAsync(Record("tx-1"), async (_, token) =>
			{
				await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (_, _) => Task.FromResult(snapshot), Deliver, token);
				updated.SetResult(true);
				await release.Task;
				throw new InvalidOperationException("grant failed after the subscription update");
			}, CancellationToken.None);
			await updated.Task;
			SubscriptionUpdate seen = null;
			var waiting = ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (stored, _) =>
			{
				seen = stored;
				return Task.FromResult<SubscriptionUpdate>(null);
			}, Deliver, CancellationToken.None);
			await Task.Delay(100);
			Assert.False(waiting.IsCompleted);
			release.SetResult(true);

			await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
			await waiting;
			Assert.Null(seen);

			await ledger.RedeemAsync(Record("tx-2"), async (_, token) =>
			{
				await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (_, _) => Task.FromResult(snapshot), Deliver, token);
				return LedgerGrant.Granted(null);
			}, CancellationToken.None);
			await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (stored, _) =>
			{
				seen = stored;
				return Task.FromResult<SubscriptionUpdate>(null);
			}, Deliver, CancellationToken.None);
			Assert.Equal(snapshot, seen);
		}

		[Fact]
		public async Task Subscription_update_left_running_after_its_grant_is_refused()
		{
			// 지급 콜백이 기다리지 않은 작업이 지급이 끝난 뒤 구독을 갱신하면 예외이고, 구독 잠금이 새지 않는다(리뷰 2b 9회차)
			var ledger = new InMemoryPurchaseLedger();
			var update = new SubscriptionUpdate { Store = IapStoreNames.Fake, SubscriptionId = "sub-1", State = IapSubscriptionState.Active };
			Task Deliver(SubscriptionUpdate _, CancellationToken token) => Task.CompletedTask;
			var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			Task orphan = null;

			await ledger.RedeemAsync(Record("tx-1"), (_, token) =>
			{
				orphan = Task.Run(async () =>
				{
					await signal.Task;
					await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (_, _) => Task.FromResult(update), Deliver, CancellationToken.None);
				});
				return Task.FromResult(LedgerGrant.Granted(null));
			}, CancellationToken.None);
			signal.SetResult(true);

			await Assert.ThrowsAsync<InvalidOperationException>(() => orphan);
			var next = ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (_, _) => Task.FromResult(update), Deliver, CancellationToken.None);
			Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(5))));
			Assert.Equal(SubscriptionUpdateOutcome.Delivered, await next);
		}

		[Fact]
		public async Task Cancelled_wait_for_a_subscription_inside_a_grant_does_not_leak_the_lock()
		{
			var ledger = new InMemoryPurchaseLedger();
			var update = new SubscriptionUpdate { Store = IapStoreNames.Fake, SubscriptionId = "sub-1", State = IapSubscriptionState.Active };
			Task Deliver(SubscriptionUpdate _, CancellationToken token) => Task.CompletedTask;
			var holding = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var holder = ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", async (_, _) =>
			{
				holding.SetResult(true);
				await release.Task;
				return update;
			}, Deliver, CancellationToken.None);
			await holding.Task;
			using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ledger.RedeemAsync(Record("tx-1"), async (_, token) =>
			{
				await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (_, _) => Task.FromResult(update), Deliver, cancel.Token);
				return LedgerGrant.Granted(null);
			}, CancellationToken.None));
			release.SetResult(true);
			await holder;

			var next = ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (_, _) => Task.FromResult(update), Deliver, CancellationToken.None);
			Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(5))));
			Assert.Null(await ledger.FindAsync(IapStoreNames.Fake, "tx-1", CancellationToken.None));
		}

		[Fact]
		public async Task Concurrent_subscription_updates_inside_one_grant_do_not_deadlock()
		{
			// 한 지급 콜백이 같은 구독의 갱신 둘을 동시에 기다려도, 지급 안의 갱신을 하나씩 해 교착하지 않는다(리뷰 2b 10회차)
			var ledger = new InMemoryPurchaseLedger();
			SubscriptionUpdate Update(IapSubscriptionState state) => new SubscriptionUpdate { Store = IapStoreNames.Fake, SubscriptionId = "sub-1", State = state };
			Task Deliver(SubscriptionUpdate _, CancellationToken token) => Task.CompletedTask;
			var holding = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var holder = ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", async (_, _) =>
			{
				holding.SetResult(true);
				await release.Task;
				return null;
			}, Deliver, CancellationToken.None);
			await holding.Task;

			var redeem = ledger.RedeemAsync(Record("tx-1"), async (_, token) =>
			{
				await Task.WhenAll(
					ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (_, _) => Task.FromResult(Update(IapSubscriptionState.Active)), Deliver, token),
					ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (stored, _) => Task.FromResult(stored with { State = IapSubscriptionState.Canceled }),
						Deliver, token));
				return LedgerGrant.Granted(null);
			}, CancellationToken.None);
			await Task.Delay(100);
			release.SetResult(true);
			await holder;

			Assert.Same(redeem, await Task.WhenAny(redeem, Task.Delay(TimeSpan.FromSeconds(5))));
			Assert.Equal(LedgerRedeemOutcome.Recorded, (await redeem).Outcome);
			SubscriptionUpdate seen = null;
			var next = ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (stored, _) =>
			{
				seen = stored;
				return Task.FromResult<SubscriptionUpdate>(null);
			}, Deliver, CancellationToken.None);
			Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(5))));
			Assert.Equal(IapSubscriptionState.Canceled, seen.State);
		}

		[Fact]
		public async Task Cancelled_wait_for_the_grant_serial_does_not_break_later_updates()
		{
			// 같은 지급 안에서 앞 갱신을 기다리던 갱신이 취소되면 그 콜백은 불리지 않고, 앞 갱신과 이어지는 갱신은 그대로 진행된다(리뷰 2b 11회차)
			var ledger = new InMemoryPurchaseLedger();
			var update = new SubscriptionUpdate { Store = IapStoreNames.Fake, SubscriptionId = "sub-1", State = IapSubscriptionState.Active };
			Task Deliver(SubscriptionUpdate _, CancellationToken token) => Task.CompletedTask;
			var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var secondCalled = false;
			using var cancel = new CancellationTokenSource();

			var redeem = ledger.RedeemAsync(Record("tx-1"), async (_, token) =>
			{
				var first = ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", async (_, _) =>
				{
					firstEntered.SetResult(true);
					await releaseFirst.Task;
					return update;
				}, Deliver, token);
				await firstEntered.Task;
				var second = ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (_, _) =>
				{
					secondCalled = true;
					return Task.FromResult(update);
				}, Deliver, cancel.Token);
				cancel.Cancel();
				await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
				releaseFirst.SetResult(true);
				await first;
				Assert.Equal(SubscriptionUpdateOutcome.Delivered, await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1",
					(stored, _) => Task.FromResult(stored with { State = IapSubscriptionState.Canceled }), Deliver, token));
				return LedgerGrant.Granted(null);
			}, CancellationToken.None);

			Assert.Same(redeem, await Task.WhenAny(redeem, Task.Delay(TimeSpan.FromSeconds(5))));
			Assert.Equal(LedgerRedeemOutcome.Recorded, (await redeem).Outcome);
			Assert.False(secondCalled);
			SubscriptionUpdate seen = null;
			await ledger.UpdateSubscriptionAsync(IapStoreNames.Fake, "sub-1", (stored, _) =>
			{
				seen = stored;
				return Task.FromResult<SubscriptionUpdate>(null);
			}, Deliver, CancellationToken.None);
			Assert.Equal(IapSubscriptionState.Canceled, seen.State);
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
