using System;
using System.Collections;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Tests
{
	public sealed class IapServicePurchaseTest : IapServiceTestBase
	{
		[Test]
		public void Purchase_FulfillsThenConfirms_AndReportsOnce()
		{
			Initialize();
			CompletePurchasesImmediately();

			var result = Purchase(Gem, "payload-1");

			Assert.That(result.Done, Is.True);
			Assert.That(result.Value.IsSuccess, Is.True, result.Value.ToString());
			var settlement = result.Value.Value;
			Assert.That(settlement.IsConfirmed, Is.True);
			Assert.That(settlement.IsAwaited, Is.True);
			Assert.That(settlement.Fulfillment.Status, Is.EqualTo(FulfillmentStatus.Granted));
			Assert.That(settlement.Fulfillment.Response, Is.EqualTo("reward"));

			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1));
			var order = Fulfiller.Calls[0];
			Assert.That(order.Origin, Is.EqualTo(IapOrderOrigin.Purchase));
			Assert.That(order.Payload, Is.EqualTo("payload-1"));
			Assert.That(order.ProductId, Is.EqualTo(Gem.Id));
			Assert.That(order.StoreProductId, Is.EqualTo(Gem.StoreSpecificId));
			Assert.That(order.Price, Is.EqualTo(1100m));
			Assert.That(order.CurrencyCode, Is.EqualTo("KRW"));
			Assert.That(order.StoreName, Is.EqualTo("FakeStore"));
			Assert.That(order.SignedTransaction, Does.StartWith("jws-"));
			Assert.That(order.Receipt, Does.StartWith("receipt-"));

			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { order.TransactionId }));
			Assert.That(Store.Pending, Is.Empty);
			Assert.That(Confirmed, Has.Count.EqualTo(1));
			Assert.That(Settled, Is.EqualTo(new[] { settlement }));
			Assert.That(Failures, Is.Empty);
			Assert.That(StoreUi, Is.EqualTo(new[] { true, false }));
		}

		[Test]
		public void Purchase_DoesNotConfirmBeforeTheFulfillerFinishes()
		{
			Initialize();
			CompletePurchasesImmediately();
			var hold = Fulfiller.Hold();

			var result = Purchase(Gem);

			Assert.That(result.Done, Is.False);
			Assert.That(Store.ConfirmCalls, Is.Empty);

			hold.TrySetResult(FulfillmentResult.Granted());

			Assert.That(result.Done, Is.True);
			Assert.That(result.Value.IsSuccess, Is.True);
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
		}

		[Test]
		public void AlreadyGranted_ClosesTheTransaction()
		{
			Initialize();
			CompletePurchasesImmediately();
			Fulfiller.Next(FulfillmentResult.AlreadyGranted(null, "duplicate receipt"));

			var result = Purchase(Gem);

			Assert.That(result.Value.IsSuccess, Is.True);
			Assert.That(result.Value.Value.Fulfillment.Status, Is.EqualTo(FulfillmentStatus.AlreadyGranted));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
			Assert.That(Store.Pending, Is.Empty);
		}

		[Test]
		public void RetryLater_KeepsTheTransactionOpen_AndRetriesWithBackoff()
		{
			Initialize();
			CompletePurchasesImmediately();
			Fulfiller.Next(FulfillmentResult.RetryLater("server down"));

			var result = Purchase(Gem, "p");

			Assert.That(result.Value.Code, Is.EqualTo(IapResultCodes.FulfillmentPending));
			Assert.That(result.Value.Message, Does.Contain("server down"));
			Assert.That(Store.ConfirmCalls, Is.Empty);
			Assert.That(Store.Pending, Has.Count.EqualTo(1));
			Assert.That(Failures, Has.Count.EqualTo(1));
			Assert.That(Failures[0].Code, Is.EqualTo(IapResultCodes.FulfillmentPending));
			Assert.That(Failures[0].WillRetry, Is.True);
			Assert.That(Failures[0].Attempts, Is.EqualTo(1));
			Assert.That(Delay.PendingCount(RetryDelay), Is.EqualTo(1));

			Clock.Elapsed += RetryDelay;
			Delay.Complete(RetryDelay);

			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(2));
			Assert.That(Fulfiller.Calls[1].TransactionId, Is.EqualTo(Fulfiller.Calls[0].TransactionId));
			Assert.That(Fulfiller.Calls[1].Payload, Is.EqualTo("p"));
			Assert.That(Fulfiller.Calls[1].Origin, Is.EqualTo(IapOrderOrigin.Purchase));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
			Assert.That(Settled, Has.Count.EqualTo(1));
			Assert.That(Settled[0].IsAwaited, Is.False);
		}

		[Test]
		public void RetryDelay_DoublesUpToTheMaximum()
		{
			Initialize();
			CompletePurchasesImmediately();
			Fulfiller.Default = (order, token) => UniTask.FromResult(FulfillmentResult.RetryLater());

			Purchase(Gem);

			foreach (var seconds in new[] { 5, 10, 20, 40, 40 })
			{
				var delay = TimeSpan.FromSeconds(seconds);
				Assert.That(Delay.PendingCount(delay), Is.EqualTo(1), $"waiting {seconds}s");
				Clock.Elapsed += delay;
				Delay.Complete(delay);
			}

			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(6));
			Assert.That(Failures[Failures.Count - 1].Attempts, Is.EqualTo(6));
			Assert.That(Store.ConfirmCalls, Is.Empty);
		}

		[Test]
		public void Rejected_LeavesTheTransactionOpen_WithoutAutomaticRetry()
		{
			Initialize();
			CompletePurchasesImmediately();
			Fulfiller.Next(FulfillmentResult.Rejected("forged"));

			var result = Purchase(Gem);

			Assert.That(result.Value.Code, Is.EqualTo(IapResultCodes.FulfillmentRejected));
			Assert.That(Store.ConfirmCalls, Is.Empty);
			Assert.That(Store.Pending, Has.Count.EqualTo(1));
			Assert.That(Delay.PendingCount(RetryDelay), Is.Zero);
			Assert.That(Failures[0].WillRetry, Is.False);

			var recovered = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync());

			Assert.That(recovered.Value.IsSuccess, Is.True);
			Assert.That(recovered.Value.Value.Granted, Is.EqualTo(1));
			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(2));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
		}

		[Test]
		public void FulfillerException_IsLoggedAndRetried()
		{
			Initialize();
			CompletePurchasesImmediately();
			Fulfiller.Next((order, token) => throw new InvalidOperationException("boom"));
			LogAssert.Expect(LogType.Error, new Regex("Fulfilling .* threw"));
			LogAssert.Expect(LogType.Exception, new Regex("boom"));

			var result = Purchase(Gem);

			Assert.That(result.Value.Code, Is.EqualTo(IapResultCodes.FulfillmentPending));
			Assert.That(Store.ConfirmCalls, Is.Empty);

			Clock.Elapsed += RetryDelay;
			Delay.Complete(RetryDelay);

			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
		}

		[Test]
		public void FulfillerTimeout_KeepsTheTransactionOpen_AndALateGrantIsNotConfirmed()
		{
			Initialize();
			CompletePurchasesImmediately();
			var hold = Fulfiller.Hold();

			var result = Purchase(Gem);
			Assert.That(Delay.Complete(FulfillTimeout), Is.EqualTo(1));

			Assert.That(result.Value.Code, Is.EqualTo(IapResultCodes.FulfillmentPending));
			Assert.That(Fulfiller.Tokens[0].IsCancellationRequested, Is.True);

			hold.TrySetResult(FulfillmentResult.Granted());
			Assert.That(Store.ConfirmCalls, Is.Empty, "a result after the timeout is not trusted; the retry asks the server again");

			Clock.Elapsed += RetryDelay;
			Delay.Complete(RetryDelay);

			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(2));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
		}

		[Test]
		public void UndefinedFulfillmentStatus_IsRetried()
		{
			Initialize();
			CompletePurchasesImmediately();
			Fulfiller.Next(default(FulfillmentResult));
			LogAssert.Expect(LogType.Error, new Regex("undefined status 0"));

			var result = Purchase(Gem);

			Assert.That(result.Value.Code, Is.EqualTo(IapResultCodes.FulfillmentPending));
			Assert.That(Store.ConfirmCalls, Is.Empty);
		}

		[Test]
		public void ConfirmFailure_ReportsTheGrant_AndRetriesOnlyTheConfirmation()
		{
			Initialize();
			CompletePurchasesImmediately();
			var attempts = 0;
			Store.ConfirmHandler = order =>
			{
				if (++attempts == 1)
				{
					return UniTask.FromResult(IapConfirmResult.Failed(IapStoreFailureReason.StoreUnavailable, "offline"));
				}

				Store.Pending.RemoveAll(pending => pending.TransactionId == order.TransactionId);
				return UniTask.FromResult(IapConfirmResult.Confirmed());
			};

			var result = Purchase(Gem);

			Assert.That(result.Value.IsSuccess, Is.True, "the player got the reward, so the purchase succeeded");
			Assert.That(result.Value.Value.IsConfirmed, Is.False);
			Assert.That(Settled, Has.Count.EqualTo(1));
			Assert.That(Confirmed, Is.Empty);
			Assert.That(Failures[0].Code, Is.EqualTo(IapResultCodes.ConfirmFailed));
			Assert.That(Failures[0].WillRetry, Is.True);

			Clock.Elapsed += RetryDelay;
			Delay.Complete(RetryDelay);

			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1), "the fulfiller is not asked again");
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(2));
			Assert.That(Confirmed, Has.Count.EqualTo(1));
			Assert.That(Settled, Has.Count.EqualTo(1), "the settlement is announced once");
		}

		[Test]
		public void ConfirmTimeout_IsRetried()
		{
			Initialize();
			CompletePurchasesImmediately();
			var attempts = 0;
			Store.ConfirmHandler = order => ++attempts == 1
				? new UniTaskCompletionSource<IapConfirmResult>().Task
				: UniTask.FromResult(IapConfirmResult.Confirmed());

			var result = Purchase(Gem);
			Assert.That(Delay.Complete(ConfirmTimeout), Is.EqualTo(1));

			Assert.That(result.Value.IsSuccess, Is.True);
			Assert.That(result.Value.Value.IsConfirmed, Is.False);

			Clock.Elapsed += RetryDelay;
			Delay.Complete(RetryDelay);

			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(2));
			Assert.That(Confirmed, Has.Count.EqualTo(1));
		}

		[Test]
		public void AlreadyConfirmedInTheStore_ClosesWithoutRevenue()
		{
			Initialize();
			CompletePurchasesImmediately();
			Store.ConfirmHandler = order => UniTask.FromResult(IapConfirmResult.AlreadyConfirmed("acknowledged elsewhere"));

			var result = Purchase(Gem);

			Assert.That(result.Value.IsSuccess, Is.True);
			Assert.That(result.Value.Value.IsConfirmed, Is.True);
			Assert.That(Confirmed, Is.Empty);
			Assert.That(Settled, Has.Count.EqualTo(1));
			Assert.That(Delay.PendingCount(RetryDelay), Is.Zero);
		}

		[Test]
		public void StoreTimeout_ReturnsTimeout_AndALatePaymentIsFulfilledInTheBackground()
		{
			Initialize();

			var result = Purchase(Gem, "late");

			Assert.That(result.Done, Is.False);
			Assert.That(Service.IsStoreUiOpen, Is.True);
			Assert.That(Delay.Complete(StoreTimeout), Is.EqualTo(1));

			Assert.That(result.Value.Code, Is.EqualTo(ResultCodes.Timeout));
			Assert.That(StoreUi, Is.EqualTo(new[] { true, false }));

			Store.CompletePurchase(Gem.Id);

			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1));
			Assert.That(Fulfiller.Calls[0].Origin, Is.EqualTo(IapOrderOrigin.Purchase));
			Assert.That(Fulfiller.Calls[0].Payload, Is.EqualTo("late"));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
			Assert.That(Settled[0].IsAwaited, Is.False);
		}

		[Test]
		public void CancellingTheWait_ReleasesThePurchase_AndALatePaymentIsFulfilled()
		{
			Initialize();
			var cancellation = new CancellationTokenSource();
			var result = Captured<Result<PurchaseSettlement>>.Of(Service.PurchaseAsync(Gem.Id, "c", cancellation.Token));

			cancellation.Cancel();

			Assert.That(result.Done, Is.True);
			Assert.That(result.Value.Code, Is.EqualTo(ResultCodes.Cancelled));
			Assert.That(Service.IsStoreUiOpen, Is.False);

			Store.CompletePurchase(Gem.Id);

			Assert.That(Fulfiller.Calls[0].Payload, Is.EqualTo("c"));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));

			CompletePurchasesImmediately();
			Assert.That(Purchase(Coin).Value.IsSuccess, Is.True, "the purchase lock is released");
		}

		[Test]
		public void SecondPurchaseWhileOneIsInProgress_ReturnsInProgress()
		{
			Initialize();

			var first = Purchase(Gem);
			var second = Purchase(Coin);

			Assert.That(first.Done, Is.False);
			Assert.That(second.Value.Code, Is.EqualTo(ResultCodes.InProgress));
			Assert.That(Store.Purchases, Has.Count.EqualTo(1));
		}

		[Test]
		public void UserCancel_ReturnsUserCancelled_WithoutFulfilling()
		{
			Initialize();
			Store.OnPurchase = definition => Store.RaisePurchaseFailed(definition.Id, IapStoreFailureReason.UserCancelled);

			var result = Purchase(Gem);

			Assert.That(result.Value.Code, Is.EqualTo(IapResultCodes.UserCancelled));
			Assert.That(Fulfiller.Calls, Is.Empty);
			Assert.That(StoreUi, Is.EqualTo(new[] { true, false }));
		}

		[TestCase(IapStoreFailureReason.StoreUnavailable, IapResultCodes.StoreUnavailable)]
		[TestCase(IapStoreFailureReason.ExistingPurchasePending, IapResultCodes.ExistingPurchasePending)]
		[TestCase(IapStoreFailureReason.ProductUnavailable, IapResultCodes.ProductUnavailable)]
		[TestCase(IapStoreFailureReason.ValidationFailed, IapResultCodes.StoreValidationFailed)]
		[TestCase(IapStoreFailureReason.PaymentDeclined, IapResultCodes.PaymentDeclined)]
		[TestCase(IapStoreFailureReason.Unknown, IapResultCodes.StoreError)]
		public void StoreFailures_MapToResultCodes(IapStoreFailureReason reason, int code)
		{
			Initialize();
			Store.OnPurchase = definition => Store.RaisePurchaseFailed(definition.Id, reason);

			Assert.That(Purchase(Gem).Value.Code, Is.EqualTo(code));
		}

		[Test]
		public void FailureForAnotherProduct_DoesNotEndThePurchase()
		{
			Initialize();
			var result = Purchase(Gem);

			Store.RaisePurchaseFailed(Coin.Id, IapStoreFailureReason.UserCancelled);

			Assert.That(result.Done, Is.False);
		}

		[Test]
		public void Deferred_ReturnsDeferred_AndTheApprovedPaymentIsFulfilledLater()
		{
			Initialize();
			Store.OnPurchase = definition => Store.RaisePurchaseDeferred(definition.Id);

			var result = Purchase(Gem, "approval");

			Assert.That(result.Value.Code, Is.EqualTo(IapResultCodes.Deferred));
			Assert.That(Fulfiller.Calls, Is.Empty);

			Store.CompletePurchase(Gem.Id);

			Assert.That(Fulfiller.Calls[0].Origin, Is.EqualTo(IapOrderOrigin.Purchase));
			Assert.That(Fulfiller.Calls[0].Payload, Is.EqualTo("approval"));
			Assert.That(Settled[0].IsAwaited, Is.False);
		}

		[Test]
		public void DuplicateTransaction_FulfillsTheUnfinishedOrderOfTheSameProduct()
		{
			Initialize();
			var old = Store.AddPending(Gem.Id);
			Store.OnPurchase = definition => Store.RaisePurchaseFailed(definition.Id, IapStoreFailureReason.DuplicateTransaction);

			var result = Purchase(Gem);

			Assert.That(result.Value.IsSuccess, Is.True, result.Value.ToString());
			Assert.That(result.Value.Value.Order.TransactionId, Is.EqualTo(old.TransactionId));
			Assert.That(result.Value.Value.Order.Origin, Is.EqualTo(IapOrderOrigin.Recovered));
			Assert.That(result.Value.Value.IsAwaited, Is.True);
			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { old.TransactionId }));
		}

		[Test]
		public void DuplicateTransaction_WithoutAnUnfinishedOrder_ReturnsDuplicateTransaction()
		{
			Initialize();
			Store.OnPurchase = definition => Store.RaisePurchaseFailed(definition.Id, IapStoreFailureReason.DuplicateTransaction);

			Assert.That(Purchase(Gem).Value.Code, Is.EqualTo(IapResultCodes.DuplicateTransaction));
		}

		[Test]
		public void UnfinishedOrderOfTheSameProduct_IsFulfilledInsteadOfOpeningTheStore()
		{
			Initialize();
			CompletePurchasesImmediately();
			Fulfiller.Next(FulfillmentResult.RetryLater());
			var first = Purchase(Gem);
			Assert.That(first.Value.Code, Is.EqualTo(IapResultCodes.FulfillmentPending));

			var second = Purchase(Gem);

			Assert.That(Store.Purchases, Has.Count.EqualTo(1), "the store is not opened again");
			Assert.That(second.Value.IsSuccess, Is.True);
			Assert.That(second.Value.Value.Order.TransactionId, Is.EqualTo(Fulfiller.Calls[0].TransactionId));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
		}

		[Test]
		public void FulfilledButUnconfirmedOrder_IsClosedBeforeBuyingAgain()
		{
			Initialize();
			CompletePurchasesImmediately();
			var attempts = 0;
			Store.ConfirmHandler = order =>
			{
				if (++attempts == 1)
				{
					return UniTask.FromResult(IapConfirmResult.Failed(IapStoreFailureReason.StoreUnavailable, "offline"));
				}

				Store.Pending.RemoveAll(pending => pending.TransactionId == order.TransactionId);
				return UniTask.FromResult(IapConfirmResult.Confirmed());
			};
			var first = Purchase(Gem);
			Assert.That(first.Value.Value.IsConfirmed, Is.False);

			var second = Purchase(Gem);

			Assert.That(second.Value.IsSuccess, Is.True);
			Assert.That(Store.Purchases, Has.Count.EqualTo(2), "a new payment is opened after the old one is closed");
			Assert.That(second.Value.Value.Order.TransactionId, Is.Not.EqualTo(first.Value.Value.Order.TransactionId));
			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(2), "the old order is not fulfilled twice");
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(3));
		}

		[Test]
		public void SameTransactionDeliveredTwice_IsFulfilledOnce()
		{
			Initialize();
			var hold = Fulfiller.Hold();
			var order = Store.AddPending(Gem.Id);

			Store.RaiseOrderPending(order);
			Store.RaiseOrderPending(order);

			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1));

			hold.TrySetResult(FulfillmentResult.Granted());
			Store.RaiseOrderPending(order);

			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1), "a closed transaction is ignored");
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
		}

		[Test]
		public void Purchase_IsProcessedBeforeQueuedRecoveredOrders()
		{
			Initialize();
			var hold = Fulfiller.Hold();
			var first = Store.AddPending(Coin.Id);
			Store.RaiseOrderPending(first);
			var second = Store.AddPending(Coin.Id);
			Store.RaiseOrderPending(second);
			CompletePurchasesImmediately();

			var result = Purchase(Gem);
			hold.TrySetResult(FulfillmentResult.Granted());

			Assert.That(result.Value.IsSuccess, Is.True);
			Assert.That(Fulfiller.Calls[0].TransactionId, Is.EqualTo(first.TransactionId));
			Assert.That(Fulfiller.Calls[1].ProductId, Is.EqualTo(Gem.Id));
			Assert.That(Fulfiller.Calls[2].TransactionId, Is.EqualTo(second.TransactionId));
		}

		[Test]
		public void Misuse_Throws_AndNormalFailuresReturnCodes()
		{
			Assert.That(Captured<Result<PurchaseSettlement>>.Of(Service.PurchaseAsync("unknown")).Error, Is.TypeOf<ArgumentException>());
			Assert.That(Captured<Result<PurchaseSettlement>>.Of(Service.PurchaseAsync(" ")).Error, Is.TypeOf<ArgumentException>());
			Assert.That(Purchase(Gem).Value.Code, Is.EqualTo(IapResultCodes.NotInitialized));

			Store.MissingProducts.Add(Coin.Id);
			Store.UnavailableProducts.Add(NoAds.Id);
			Initialize();

			Assert.That(Purchase(Coin).Value.Code, Is.EqualTo(IapResultCodes.ProductNotFound));
			Assert.That(Purchase(NoAds).Value.Code, Is.EqualTo(IapResultCodes.ProductUnavailable));
			Assert.That(Store.Purchases, Is.Empty);
		}

		[Test]
		public void EventHandlerException_DoesNotBreakTheSettlement()
		{
			Initialize();
			CompletePurchasesImmediately();
			Service.OrderSettled += settlement => throw new InvalidOperationException("handler boom");
			LogAssert.Expect(LogType.Error, new Regex("OrderSettled handler threw"));
			LogAssert.Expect(LogType.Exception, new Regex("handler boom"));

			var result = Purchase(Gem);

			Assert.That(result.Value.IsSuccess, Is.True);
			Assert.That(Settled, Has.Count.EqualTo(1));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
		}

		[UnityTest]
		public IEnumerator FulfillerFinishingOnAnotherThread_ConfirmsOnTheMainThread()
		{
			var mainThread = Thread.CurrentThread.ManagedThreadId;
			Initialize();
			CompletePurchasesImmediately();
			var hold = Fulfiller.Hold();
			var result = Purchase(Gem);

			Task.Run(() => hold.TrySetResult(FulfillmentResult.Granted())).Wait();

			for (var frame = 0; frame < 100 && !result.Done; frame++)
			{
				yield return null;
			}

			Assert.That(result.Done, Is.True);
			Assert.That(result.Value.IsSuccess, Is.True);
			Assert.That(Store.ConfirmThreads, Is.EqualTo(new[] { mainThread }));
		}
	}
}
