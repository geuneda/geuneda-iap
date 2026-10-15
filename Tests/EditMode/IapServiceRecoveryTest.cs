using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Tests
{
	public sealed class IapServiceRecoveryTest : IapServiceTestBase
	{
		[Test]
		public void Initialize_FetchesProductsBeforeOrders_AndFulfillsLeftoverPayments()
		{
			var leftover = Store.AddPending(Gem.Id);

			Initialize();

			Assert.That(Store.Calls, Is.EqualTo(new[] { "products", "orders" }));
			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1));
			Assert.That(Fulfiller.Calls[0].TransactionId, Is.EqualTo(leftover.TransactionId));
			Assert.That(Fulfiller.Calls[0].Origin, Is.EqualTo(IapOrderOrigin.Recovered));
			Assert.That(Fulfiller.Calls[0].Payload, Is.Null);
			Assert.That(Fulfiller.Calls[0].Price, Is.EqualTo(1100m));
			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { leftover.TransactionId }));
			Assert.That(Settled, Has.Count.EqualTo(1));
			Assert.That(Settled[0].IsAwaited, Is.False);
		}

		[Test]
		public void OrdersArrivingBeforeInitialization_WaitForTheProducts()
		{
			var early = Store.AddPending(Gem.Id);
			Store.RaiseOrderPending(early);

			Assert.That(Fulfiller.Calls, Is.Empty, "nothing is fulfilled before the products are known");

			Initialize();

			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(1));
			Assert.That(Fulfiller.Calls[0].Price, Is.EqualTo(1100m), "the order is rebuilt with the product price");
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
		}

		[Test]
		public void OrderOfAProductTheStoreDoesNotKnow_IsNeitherFulfilledNorConfirmed()
		{
			Store.AddPending(Gem.Id, false);
			LogAssert.Expect(LogType.Error, new Regex("unknown to the store or the definitions"));

			Initialize();

			Assert.That(Fulfiller.Calls, Is.Empty);
			Assert.That(Store.ConfirmCalls, Is.Empty);
			Assert.That(Failures, Has.Count.EqualTo(1));
			Assert.That(Failures[0].Code, Is.EqualTo(IapResultCodes.UnknownProduct));
			Assert.That(Failures[0].WillRetry, Is.False);

			var recovered = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync());

			Assert.That(recovered.Value.Value.Unresolved, Is.EqualTo(1));
			Assert.That(Failures, Has.Count.EqualTo(1), "reported once");
		}

		[Test]
		public void OrderOfAnUndefinedProduct_IsNeitherFulfilledNorConfirmed()
		{
			Store.AddPending("retired_pack");
			LogAssert.Expect(LogType.Error, new Regex("Keep retired products"));

			Initialize();

			Assert.That(Fulfiller.Calls, Is.Empty);
			Assert.That(Store.ConfirmCalls, Is.Empty);
			Assert.That(Failures[0].Order.ProductId, Is.EqualTo("retired_pack"));
			Assert.That(Failures[0].Order.Definition, Is.Null);
		}

		[Test]
		public void UnknownOrder_IsFulfilledOnceTheStoreKnowsTheProduct()
		{
			var order = Store.AddPending(Gem.Id, false);
			LogAssert.Expect(LogType.Error, new Regex("unknown to the store"));
			Initialize();

			Store.Pending.Clear();
			Store.Pending.Add(new IapStoreOrder(Gem.Id, order.TransactionId, order.Receipt, true, order.Native));
			var recovered = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync());

			Assert.That(recovered.Value.Value.Granted, Is.EqualTo(1));
			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { order.TransactionId }));
		}

		[Test]
		public void StoreIdOfTheOrder_IsMappedToTheDefinition()
		{
			var order = Store.AddPending(NoAds.StoreSpecificId);

			Initialize();

			Assert.That(Fulfiller.Calls[0].ProductId, Is.EqualTo(NoAds.Id));
			Assert.That(Fulfiller.Calls[0].StoreProductId, Is.EqualTo(NoAds.StoreSpecificId));
			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { order.TransactionId }));
		}

		[Test]
		public void Resume_FetchesTheStoreAgain()
		{
			Initialize();
			var later = Store.AddPending(Coin.Id);

			Service.HandlePause(true);
			Service.HandlePause(false);

			Assert.That(Store.FetchOrdersCalls, Is.EqualTo(2));
			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { later.TransactionId }));
		}

		[Test]
		public void Resume_RetriesAWaitingOrderWithoutWaitingForTheBackoff()
		{
			Initialize();
			Fulfiller.Next(FulfillmentResult.RetryLater());
			Store.RaiseOrderPending(Store.AddPending(Gem.Id));
			Assert.That(Store.ConfirmCalls, Is.Empty);

			Service.HandlePause(true);
			Service.HandlePause(false);

			Assert.That(Fulfiller.Calls, Has.Count.EqualTo(2));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
		}

		[Test]
		public void ResumeFromTheStoreUi_DoesNotFetch()
		{
			Initialize();
			Purchase(Gem);

			Service.HandlePause(true);
			Service.HandlePause(false);

			Assert.That(Store.FetchOrdersCalls, Is.EqualTo(1));
		}

		[Test]
		public void ResumeWithoutPause_DoesNothing()
		{
			Initialize();

			Service.HandlePause(false);

			Assert.That(Store.FetchOrdersCalls, Is.EqualTo(1));
		}

		[Test]
		public void RecoverOnResumeOff_DoesNotFetch()
		{
			Service.Dispose();
			Service = Create(options => options.RecoverOnResume = false);
			Initialize();

			Service.HandlePause(true);
			Service.HandlePause(false);

			Assert.That(Store.FetchOrdersCalls, Is.EqualTo(1));
		}

		[Test]
		public void FetchFailure_IsReportedNotTreatedAsNoOrders()
		{
			Initialize();
			Store.OrdersHandler = () => UniTask.FromResult(Result<IapStoreOrders>.Fail(IapResultCodes.StoreUnavailable, "billing unavailable"));

			var recovered = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync());

			Assert.That(recovered.Value.IsSuccess, Is.False);
			Assert.That(recovered.Value.Code, Is.EqualTo(IapResultCodes.StoreUnavailable));
		}

		[Test]
		public void FetchTimeout_ReturnsTimeout()
		{
			Initialize();
			Store.OrdersHandler = () => new UniTaskCompletionSource<Result<IapStoreOrders>>().Task;

			var recovered = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync());
			Assert.That(Delay.Complete(FetchTimeout), Is.EqualTo(1));

			Assert.That(recovered.Value.Code, Is.EqualTo(ResultCodes.Timeout));
		}

		[Test]
		public void RecoverRequestedDuringAFetch_WaitsForANewerFetch()
		{
			Initialize();
			var first = new UniTaskCompletionSource<Result<IapStoreOrders>>();
			Store.OrdersHandler = () => first.Task;
			var a = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync());
			Store.OrdersHandler = null;
			var b = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync());
			var late = Store.AddPending(Gem.Id);

			first.TrySetResult(Result<IapStoreOrders>.Ok(new IapStoreOrders(new IapStoreOrder[0], new IapStoreOrder[0], 0)));

			Assert.That(a.Value.Value.Total, Is.Zero);
			Assert.That(Store.FetchOrdersCalls, Is.EqualTo(3), "the second recovery fetched again after the first fetch ended");
			Assert.That(b.Value.Value.Granted, Is.EqualTo(1));
			Assert.That(Store.ConfirmCalls, Is.EqualTo(new[] { late.TransactionId }));
		}

		[Test]
		public void RecoverAsync_SummarizesTheOutcomes()
		{
			Initialize();
			Store.AddPending(Gem.Id);
			Store.AddPending(Coin.Id);
			Store.AddPending(NoAds.Id);
			Store.AddPending("retired_pack");
			Fulfiller.Next(FulfillmentResult.Granted());
			Fulfiller.Next(FulfillmentResult.RetryLater());
			Fulfiller.Next(FulfillmentResult.Rejected());
			LogAssert.Expect(LogType.Error, new Regex("Keep retired products"));

			var recovered = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync());

			var summary = recovered.Value.Value;
			Assert.That(summary.Granted, Is.EqualTo(1));
			Assert.That(summary.Retrying, Is.EqualTo(1));
			Assert.That(summary.Rejected, Is.EqualTo(1));
			Assert.That(summary.Unresolved, Is.EqualTo(1));
		}

		[Test]
		public void RecoverAsync_CanBeCancelledWhileWaiting()
		{
			Initialize();
			Fulfiller.Hold();
			Store.AddPending(Gem.Id);
			var cancellation = new CancellationTokenSource();
			var recovered = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync(cancellation.Token));
			Assert.That(recovered.Done, Is.False);

			cancellation.Cancel();

			Assert.That(recovered.Value.Code, Is.EqualTo(ResultCodes.Cancelled));
		}

		[Test]
		public void InitializationFailure_IsRetriedOnResume_AndThenRecovers()
		{
			Store.IsConnected = false;
			Store.ConnectHandler = () => UniTask.FromResult(Result.Fail(IapResultCodes.StoreUnavailable, "no network"));
			Store.AddPending(Gem.Id);

			var failed = Captured<Result>.Of(Service.InitializeAsync());

			Assert.That(failed.Value.Code, Is.EqualTo(IapResultCodes.StoreUnavailable));
			Assert.That(Service.IsInitialized, Is.False);

			Store.ConnectHandler = null;
			Service.HandlePause(true);
			Service.HandlePause(false);

			Assert.That(Service.IsInitialized, Is.True);
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
		}

		[Test]
		public void ConnectTimeout_ReturnsStoreUnavailable()
		{
			Store.IsConnected = false;
			Store.ConnectHandler = () => new UniTaskCompletionSource<Result>().Task;

			var initialized = Captured<Result>.Of(Service.InitializeAsync());
			Assert.That(Delay.Complete(ConnectTimeout), Is.EqualTo(1));

			Assert.That(initialized.Value.Code, Is.EqualTo(IapResultCodes.StoreUnavailable));
		}

		[Test]
		public void ConcurrentInitialization_RunsOnce()
		{
			var products = new UniTaskCompletionSource<Result<System.Collections.Generic.IReadOnlyList<IapProduct>>>();
			Store.ProductsHandler = () => products.Task;

			var a = Captured<Result>.Of(Service.InitializeAsync());
			var b = Captured<Result>.Of(Service.InitializeAsync());
			products.TrySetResult(Result<System.Collections.Generic.IReadOnlyList<IapProduct>>.Ok(new[] { FakeStore.Product(Gem) }));

			Assert.That(a.Value.IsSuccess, Is.True);
			Assert.That(b.Value.IsSuccess, Is.True);
			Assert.That(Store.FetchProductsCalls, Is.EqualTo(1));
			Assert.That(Service.Products, Has.Count.EqualTo(1));
		}

		[Test]
		public void NoProducts_FailsInitialization()
		{
			Store.MissingProducts.UnionWith(new[] { Gem.Id, Coin.Id, NoAds.Id, Vip.Id });

			var initialized = Captured<Result>.Of(Service.InitializeAsync());

			Assert.That(initialized.Value.Code, Is.EqualTo(IapResultCodes.NoProducts));
			Assert.That(Store.FetchOrdersCalls, Is.Zero);
		}

		[Test]
		public void SomeMissingProducts_StillInitialize()
		{
			Store.MissingProducts.Add(Vip.Id);

			Initialize();

			Assert.That(Service.Products, Has.Count.EqualTo(3));
			Assert.That(Service.TryGetProduct(Vip.Id, out _), Is.False);
			Assert.That(Service.TryGetProduct(Gem.Id, out var gem), Is.True);
			Assert.That(gem.LocalizedPriceString, Is.EqualTo("₩1,100"));
		}

		[Test]
		public void DisconnectedStore_IsReconnectedBeforeTheNextFetch()
		{
			Initialize();
			Store.IsConnected = false;
			Store.RaiseDisconnected("billing service died");

			var recovered = Captured<Result<RecoverySummary>>.Of(Service.RecoverAsync());

			Assert.That(recovered.Value.IsSuccess, Is.True);
			Assert.That(Store.ConnectCalls, Is.EqualTo(1));
		}

		[Test]
		public void Restore_FulfillsLeftoversAndReturnsOwnedNonConsumables()
		{
			Initialize();
			Store.AddPending(Vip.Id);
			Store.Owned.Add(new IapStoreOrder(NoAds.StoreSpecificId, "owned-1", "r", true, null));
			Store.Owned.Add(new IapStoreOrder(Gem.Id, "owned-2", "r", true, null));
			Store.Owned.Add(new IapStoreOrder("retired", "owned-3", "r", true, null));

			var restored = Captured<Result<System.Collections.Generic.IReadOnlyList<IapOrder>>>.Of(Service.RestoreAsync());

			Assert.That(restored.Value.IsSuccess, Is.True, restored.Value.ToString());
			Assert.That(Store.RestoreCalls, Is.EqualTo(1));
			Assert.That(Store.ConfirmCalls, Has.Count.EqualTo(1));
			Assert.That(restored.Value.Value, Has.Count.EqualTo(1));
			Assert.That(restored.Value.Value[0].ProductId, Is.EqualTo(NoAds.Id));
			Assert.That(restored.Value.Value[0].Origin, Is.EqualTo(IapOrderOrigin.Restored));
			Assert.That(StoreUi, Is.EqualTo(new[] { true, false }));
		}

		[Test]
		public void RestoreFailure_IsReturned()
		{
			Initialize();
			Store.RestoreHandler = () => UniTask.FromResult(Result.Fail(IapResultCodes.StoreUnavailable, "not signed in"));

			var restored = Captured<Result<System.Collections.Generic.IReadOnlyList<IapOrder>>>.Of(Service.RestoreAsync());

			Assert.That(restored.Value.Code, Is.EqualTo(IapResultCodes.StoreUnavailable));
			Assert.That(Service.IsStoreUiOpen, Is.False);
		}

		[Test]
		public void CheckEntitlement_AsksTheStore()
		{
			Initialize();
			Store.EntitlementHandler = product => UniTask.FromResult(Result<IapEntitlementStatus>.Ok(
				product == NoAds ? IapEntitlementStatus.FullyEntitled : IapEntitlementStatus.NotEntitled));

			Assert.That(Captured<Result<IapEntitlementStatus>>.Of(Service.CheckEntitlementAsync(NoAds.Id)).Value.Value,
				Is.EqualTo(IapEntitlementStatus.FullyEntitled));
			Assert.That(Captured<Result<IapEntitlementStatus>>.Of(Service.CheckEntitlementAsync("nope")).Error,
				Is.TypeOf<System.ArgumentException>());
		}
	}
}
