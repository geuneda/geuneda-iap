using Cysharp.Threading.Tasks;
using Geuneda.Core;
using NUnit.Framework;
using UnityEngine.TestTools;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Tests
{
	/// <summary>재진입(이벤트 처리기가 서비스를 다시 부름)과 조회·결제창·재전달이 엇갈리는 경우입니다.</summary>
	public sealed class IapServiceRaceTest : IapServiceTestBase
	{
		[Test]
		public void RecoveringFromTheStoreUiClosedHandler_DoesNotStrandThePurchase()
		{
			Initialize();
			CompletePurchasesImmediately();
			Service.StoreUiChanged += open =>
			{
				if (!open)
				{
					Service.RecoverAsync().Forget();
				}
			};

			var result = Purchase(Gem, "p");

			Assert.That(result.Done, Is.True);
			Assert.That(result.Value.IsSuccess, Is.True, result.Value.ToString());
			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1));
			Assert.That(Fulfiller.Calls[0].Payload, Is.EqualTo("p"));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
			Assert.That(Purchase(Coin).Value.IsSuccess, Is.True, "the purchase lock is released");
		}

		[Test]
		public void FetchFindingThePurchaseOrderFirst_HoldsItForThePurchase()
		{
			Initialize();
			var result = Purchase(Gem, "selected");
			var order = Store.AddPending(Gem.Id);

			var recovered = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync());

			Assert.That(Fulfiller.Calls, Is.Empty, "held until the store answers the open purchase");
			Assert.That(recovered.Done, Is.False);

			Store.RaiseOrderPending(order);

			Assert.That(result.Value.IsSuccess, Is.True, result.Value.ToString());
			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1));
			Assert.That(Fulfiller.Calls[0].Payload, Is.EqualTo("selected"));
			Assert.That(Fulfiller.Calls[0].Origin, Is.EqualTo(IapOrderOrigin.Purchase));
			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { order.TransactionId }));
			Assert.That(recovered.Value.Value.Granted, Is.EqualTo(1));
		}

		[Test]
		public void HeldOrder_IsFulfilledForThePurchaseWhenTheStoreNeverAnswers()
		{
			Initialize();
			var result = Purchase(Gem, "x");
			var order = Store.AddPending(Gem.Id);
			Service.RecoverAsync().Forget();
			Assert.That(Fulfiller.Calls, Is.Empty);

			Delay.Complete(StoreTimeout);

			Assert.That(result.Value.Code, Is.EqualTo(ResultCodes.Timeout));
			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1));
			Assert.That(Fulfiller.Calls[0].Payload, Is.EqualTo("x"));
			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { order.TransactionId }));
		}

		[Test]
		public void HeldOrdersOfAFailedPurchase_AreFulfilledAsRecovered()
		{
			Initialize();
			var result = Purchase(Gem, "x");
			var order = Store.AddPending(Gem.Id);
			Service.RecoverAsync().Forget();

			Store.RaisePurchaseFailed(Gem.Id, IapStoreFailureReason.UserCancelled);

			Assert.That(result.Value.Code, Is.EqualTo(IapResultCodes.UserCancelled));
			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1));
			Assert.That(Fulfiller.Calls[0].Origin, Is.EqualTo(IapOrderOrigin.Recovered));
			Assert.That(Fulfiller.Calls[0].Payload, Is.Null);
			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { order.TransactionId }));
		}

		[Test]
		public void RepurchaseAfterAnUnansweredPurchase_FulfillsTheEarlierPaymentFirst()
		{
			Initialize();
			var first = Purchase(Gem, "A");
			Delay.Complete(StoreTimeout);
			Assert.That(first.Value.Code, Is.EqualTo(ResultCodes.Timeout));
			var late = Store.AddPending(Gem.Id);
			CompletePurchasesImmediately();

			var second = Purchase(Gem, "B");

			Assert.That(Store.Purchases, Has.Count.EqualTo(1), "no new payment while the earlier one is unresolved");
			Assert.That(second.Value.IsSuccess, Is.True, second.Value.ToString());
			Assert.That(second.Value.Value.Order.TransactionId, Is.EqualTo(late.TransactionId));
			Assert.That(second.Value.Value.Order.Payload, Is.EqualTo("A"), "the earlier payment keeps its own payload");
			Assert.That(second.Value.Value.Order.Origin, Is.EqualTo(IapOrderOrigin.Purchase));
		}

		[Test]
		public void RepurchaseAfterAnUnansweredPurchaseWithoutPayment_DropsTheEarlierPayload()
		{
			Initialize();
			Purchase(Gem, "A");
			Delay.Complete(StoreTimeout);
			CompletePurchasesImmediately();

			var second = Purchase(Gem, "B");

			Assert.That(Store.Purchases, Has.Count.EqualTo(2));
			Assert.That(second.Value.IsSuccess, Is.True);
			Assert.That(Fulfiller.Calls[0].Payload, Is.EqualTo("B"));

			Store.RaiseOrderPending(Store.AddPending(Gem.Id));

			Assert.That(Fulfiller.Calls[1].Payload, Is.Null, "the dropped payload is not attached to a stray order");
			Assert.That(Fulfiller.Calls[1].Origin, Is.EqualTo(IapOrderOrigin.Recovered));
		}

		[Test]
		public void DisposingInTheStoreUiOpenedHandler_DoesNotStartThePayment()
		{
			Initialize();
			Service.StoreUiChanged += open =>
			{
				if (open)
				{
					Service.Dispose();
				}
			};

			var result = Purchase(Gem);

			Assert.That(result.Value.Code, Is.EqualTo(ResultCodes.Cancelled));
			Assert.That(Store.Purchases, Is.Empty);
		}

		[Test]
		public void DisposingInTheStoreUiOpenedHandler_DoesNotStartTheRestore()
		{
			Initialize();
			Service.StoreUiChanged += open =>
			{
				if (open)
				{
					Service.Dispose();
				}
			};

			var result = Captured<Result<System.Collections.Generic.IReadOnlyList<IapOrder>>>.Of(Service.RestoreAsync());

			Assert.That(result.Value.Code, Is.EqualTo(ResultCodes.Cancelled));
			Assert.That(Store.RestoreCalls, Is.Zero);
		}

		[Test]
		public void UnknownTypeRedelivery_DoesNotReplaceTheValidatedOrder()
		{
			Initialize();
			var hold = Fulfiller.Hold();
			var order = Store.AddPending(Gem.Id);
			Store.RaiseOrderPending(order);

			Store.RaiseOrderPending(new IapStoreOrder(Gem.Id, order.TransactionId, order.Receipt, false, "unknown-native"));
			Store.RaiseOrderPending(new IapStoreOrder(Coin.Id, order.TransactionId, order.Receipt, true, "other-product-native"));
			hold.TrySetResult(FulfillmentResult.Granted());

			Assert.That(Store.ConfirmNatives, Is.EqualTo(new object[] { order.Native }));
		}

		[Test]
		public void RefetchedReceipt_IsUsedForTheNextAttempt()
		{
			Initialize();
			Fulfiller.Next(FulfillmentResult.RetryLater("receipt missing"));
			var first = Store.AddPending(Gem.Id);
			Store.RaiseOrderPending(first);
			Store.Pending.Clear();
			Store.Pending.Add(new IapStoreOrder(Gem.Id, first.TransactionId, "fresh-receipt", true, first.Native, "fresh-jws"));

			var recovered = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync());

			Assert.That(recovered.Value.Value.Granted, Is.EqualTo(1));
			Assert.That(Fulfiller.Calls[1].Receipt, Is.EqualTo("fresh-receipt"));
			Assert.That(Fulfiller.Calls[1].SignedTransaction, Is.EqualTo("fresh-jws"));
		}

		[Test]
		public void ConfirmAfterADisconnection_ReconnectsFirst()
		{
			Initialize();
			CompletePurchasesImmediately();
			Fulfiller.Next((order, token) =>
			{
				Store.IsConnected = false;
				return UniTask.FromResult(FulfillmentResult.Granted());
			});

			var result = Purchase(Gem);

			Assert.That(Store.ConnectCalls, Is.EqualTo(1));
			Assert.That(result.Value.Value.IsConfirmed, Is.True);
		}

		[Test]
		public void FailedReconnectBeforeConfirm_RetriesTheConfirmationOnly()
		{
			Initialize();
			CompletePurchasesImmediately();
			Store.ConnectHandler = () => UniTask.FromResult(Result.Fail(IapResultCodes.StoreUnavailable, "offline"));
			Fulfiller.Next((order, token) =>
			{
				Store.IsConnected = false;
				return UniTask.FromResult(FulfillmentResult.Granted());
			});

			var result = Purchase(Gem);

			Assert.That(result.Value.IsSuccess, Is.True);
			Assert.That(result.Value.Value.IsConfirmed, Is.False);
			Assert.That(Store.ConfirmCalls, Is.Empty);
			Assert.That(Failures[0].Code, Is.EqualTo(IapResultCodes.ConfirmFailed));

			Store.ConnectHandler = null;
			Clock.Elapsed += RetryDelay;
			Delay.Complete(RetryDelay);

			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
			Assert.That(Confirmed, Has.Count.EqualTo(1));
		}

		[Test]
		public void FailedFetch_IsRetriedInTheBackground()
		{
			Initialize();
			var failures = 1;
			Store.OrdersHandler = () => failures-- > 0
				? UniTask.FromResult(Result<IapStoreOrders>.Fail(IapResultCodes.StoreUnavailable, "billing unavailable"))
				: UniTask.FromResult(Result<IapStoreOrders>.Ok(Store.Snapshot()));
			var leftover = Store.AddPending(Gem.Id);

			Service.HandlePause(true);
			Service.HandlePause(false);

			Assert.That(Store.ConfirmCalls, Is.Empty);
			Assert.That(Delay.PendingCount(RetryDelay), Is.EqualTo(1));

			Delay.Complete(RetryDelay);

			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { leftover.TransactionId }));
		}

		[Test]
		public void DuplicateTransactionWithAFailedFetch_KeepsRecoveringInTheBackground()
		{
			Initialize();
			var failures = 1;
			Store.OrdersHandler = () => failures-- > 0
				? UniTask.FromResult(Result<IapStoreOrders>.Fail(IapResultCodes.StoreUnavailable, "billing unavailable"))
				: UniTask.FromResult(Result<IapStoreOrders>.Ok(Store.Snapshot()));
			var old = Store.AddPending(Gem.Id);
			Store.OnPurchase = definition => Store.RaisePurchaseFailed(definition.Id, IapStoreFailureReason.DuplicateTransaction);

			var result = Purchase(Gem);

			Assert.That(result.Value.Code, Is.EqualTo(IapResultCodes.DuplicateTransaction));

			Delay.Complete(RetryDelay);

			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { old.TransactionId }));
			Assert.That(Settled[0].IsAwaited, Is.False);
		}

		[Test]
		public void DisposingInTheProductsUpdatedHandler_EndsTheInitializationWait()
		{
			Service.ProductsUpdated += Service.Dispose;

			var initialized = Captured<Result>.Of(Service.InitializeAsync());

			Assert.That(initialized.Done, Is.True);
			Assert.That(initialized.Value.Code, Is.EqualTo(ResultCodes.Cancelled));
		}

		[Test]
		public void PurchaseStartedFromTheFailedResult_KeepsItsStoreUiOpen()
		{
			Initialize();
			Captured<Result<PurchaseSettlement>> second = null;
			StartAfterResult(Service.PurchaseAsync(Gem.Id), () => second = Purchase(Coin)).Forget();

			Store.RaisePurchaseFailed(Gem.Id, IapStoreFailureReason.UserCancelled);

			Assert.That(second, Is.Not.Null);
			Assert.That(second.Done, Is.False);
			Assert.That(Service.IsStoreUiOpen, Is.True, "the second purchase's store UI is open");
			Assert.That(StoreUi, Is.EqualTo(new[] { true, false, true }));
		}

		[Test]
		public void PurchaseStartedFromTheDeferredResult_KeepsItsStoreUiOpen()
		{
			Initialize();
			Captured<Result<PurchaseSettlement>> second = null;
			StartAfterResult(Service.PurchaseAsync(Gem.Id), () => second = Purchase(Coin)).Forget();

			Store.RaisePurchaseDeferred(Gem.Id);

			Assert.That(Service.IsStoreUiOpen, Is.True);
			Assert.That(StoreUi, Is.EqualTo(new[] { true, false, true }));
		}

		[Test]
		public void UnknownOrderResolvedDuringThePurchase_IsHeldForThePurchase()
		{
			Initialize();
			var result = Purchase(Gem, "selected");
			var unknown = Store.AddPending(Gem.Id, false);
			LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("unknown to the store"));
			Service.RecoverAsync().Forget();
			Store.Pending.Clear();
			var known = new IapStoreOrder(Gem.Id, unknown.TransactionId, unknown.Receipt, true, "known-native");
			Store.Pending.Add(known);

			Service.RecoverAsync().Forget();

			Assert.That(Fulfiller.Calls, Is.Empty, "held until the store answers the open purchase");

			Store.RaiseOrderPending(known);

			Assert.That(result.Value.IsSuccess, Is.True, result.Value.ToString());
			Assert.That(Fulfiller.Calls[0].Payload, Is.EqualTo("selected"));
			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { unknown.TransactionId }));
		}

		[Test]
		public void MismatchedRedelivery_IsNotTakenAsTheOpenPurchase()
		{
			Initialize();
			var hold = Fulfiller.Hold();
			var gem = Store.AddPending(Gem.Id);
			Store.RaiseOrderPending(gem);
			var coinPurchase = Purchase(Coin);

			Store.RaiseOrderPending(new IapStoreOrder(Coin.Id, gem.TransactionId, gem.Receipt, true, "wrong-native"));
			hold.TrySetResult(FulfillmentResult.Granted());

			Assert.That(coinPurchase.Done, Is.False, "the Coin purchase still waits for its own payment");

			var coin = Store.CompletePurchase(Coin.Id);

			Assert.That(coinPurchase.Value.IsSuccess, Is.True);
			Assert.That(coinPurchase.Value.Value.Order.TransactionId, Is.EqualTo(coin.TransactionId));
		}

		[Test]
		public void ClosedTransactionRedeliveredDuringANewPurchase_DoesNotEndIt()
		{
			Initialize();
			CompletePurchasesImmediately();
			var first = Purchase(Gem);
			Assert.That(first.Value.IsSuccess, Is.True);
			var tx1 = first.Value.Value.Order.TransactionId;
			Store.OnPurchase = null;

			var second = Purchase(Gem, "new-choice");
			Store.RaiseOrderPending(new IapStoreOrder(Gem.Id, tx1, "receipt", true, "redelivered"));

			Assert.That(second.Done, Is.False, "a closed transaction is not the answer to the new purchase");
			Assert.That(Service.IsStoreUiOpen, Is.True);

			var tx2 = Store.CompletePurchase(Gem.Id);

			Assert.That(second.Value.IsSuccess, Is.True);
			Assert.That(second.Value.Value.Order.TransactionId, Is.EqualTo(tx2.TransactionId));
			Assert.That(second.Value.Value.Order.Payload, Is.EqualTo("new-choice"));
		}

		[Test]
		public void UnansweredPurchase_StartsARecoveryThatFindsThePaymentOnlyInTheStore()
		{
			Initialize();
			var result = Purchase(Gem, "x");
			// 스토어에는 결제가 생겼지만 이벤트는 오지 않았습니다
			var order = Store.AddPending(Gem.Id);

			Delay.Complete(StoreTimeout);

			Assert.That(result.Value.Code, Is.EqualTo(ResultCodes.Timeout));
			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1));
			Assert.That(Fulfiller.Calls[0].Payload, Is.EqualTo("x"));
			Assert.That(Fulfiller.Calls[0].Origin, Is.EqualTo(IapOrderOrigin.Purchase));
			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { order.TransactionId }));
		}

		private static async UniTaskVoid StartAfterResult(UniTask<Result<PurchaseSettlement>> first, System.Action next)
		{
			await first;
			next();
		}

		[Test]
		public void ServiceIsDisposableThroughTheInterface()
		{
			IIapService service = Service;

			service.Dispose();

			Assert.That(() => service.TryGetProduct(Gem.Id, out _), Throws.TypeOf<System.ObjectDisposedException>());
		}
	}
}
