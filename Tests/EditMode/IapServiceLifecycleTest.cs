using System;
using System.Threading.Tasks;
using Geuneda.Core;
using NUnit.Framework;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Tests
{
	public sealed class IapServiceLifecycleTest : IapServiceTestBase
	{
		[Test]
		public void Dispose_EndsWaitingCalls_AndConfirmsNothingAfterwards()
		{
			Initialize();
			CompletePurchasesImmediately();
			var hold = Fulfiller.Hold();
			var result = Purchase(Gem);

			Service.Dispose();

			Assert.That(result.Value.Code, Is.EqualTo(ResultCodes.Cancelled));
			Assert.That(Store.HasSubscribers, Is.False);
			Assert.That(Fulfiller.Tokens[0].IsCancellationRequested, Is.True);

			hold.TrySetResult(FulfillmentResult.Granted());

			Assert.That(Store.ConfirmCalls, Is.Empty, "the transaction stays open for the next run");
			Assert.That(() => Service.TryGetProduct(Gem.Id, out _), Throws.TypeOf<ObjectDisposedException>());
			Assert.That(() => Service.Dispose(), Throws.Nothing);
		}

		[Test]
		public void Dispose_WhileTheStoreIsOpen_ClosesTheStoreUiState()
		{
			Initialize();
			var result = Purchase(Gem);

			Service.Dispose();

			Assert.That(result.Value.Code, Is.EqualTo(ResultCodes.Cancelled));
			Assert.That(StoreUi, Is.EqualTo(new[] { true, false }));
			Assert.That(Delay.PendingCount(StoreTimeout), Is.Zero);
		}

		[Test]
		public void Dispose_DuringInitialization_EndsTheWait()
		{
			var products = new Cysharp.Threading.Tasks.UniTaskCompletionSource<Result<System.Collections.Generic.IReadOnlyList<IapProduct>>>();
			Store.ProductsHandler = () => products.Task;
			var initialized = Captured<Result>.Of(Service.InitializeAsync());

			Service.Dispose();

			Assert.That(initialized.Value.Code, Is.EqualTo(ResultCodes.Cancelled));
		}

		[Test]
		public void UseFromAnotherThread_Throws()
		{
			var exception = Task.Run(() =>
			{
				try
				{
					Service.TryGetProduct(Gem.Id, out _);
					return null;
				}
				catch (Exception caught)
				{
					return caught;
				}
			}).Result;

			Assert.That(exception, Is.TypeOf<InvalidOperationException>());
		}

		[Test]
		public void InvalidOptions_Throw()
		{
			Assert.That(() => CreateWith(options => options.Products = Array.Empty<IapProductDefinition>()), Throws.ArgumentException);
			Assert.That(() => CreateWith(options => options.Products = new[] { Gem, null }), Throws.ArgumentException);
			Assert.That(() => CreateWith(options => options.Products = new[] { Gem, new IapProductDefinition(Gem.Id, IapProductType.NonConsumable, "other") }),
				Throws.ArgumentException);
			Assert.That(() => CreateWith(options => options.Products = new[] { Gem, new IapProductDefinition("other", IapProductType.Consumable, Gem.Id) }),
				Throws.ArgumentException);
			Assert.That(() => CreateWith(options => options.RetryDelay = TimeSpan.FromMilliseconds(500)), Throws.TypeOf<ArgumentOutOfRangeException>());
			Assert.That(() => CreateWith(options => options.MaxRetryDelay = TimeSpan.FromSeconds(1)), Throws.TypeOf<ArgumentOutOfRangeException>());
			Assert.That(() => CreateWith(options => options.FulfillTimeout = TimeSpan.Zero), Throws.TypeOf<ArgumentOutOfRangeException>());
			Assert.That(() => new IapService(null, Fulfiller, new IapServiceOptions { Products = new[] { Gem } }), Throws.ArgumentNullException);
			Assert.That(() => new IapService(Store, null, new IapServiceOptions { Products = new[] { Gem } }), Throws.ArgumentNullException);
		}

		[Test]
		public void InvalidDefinitions_Throw()
		{
			Assert.That(() => new IapProductDefinition(" ", IapProductType.Consumable), Throws.ArgumentException);
			Assert.That(() => new IapProductDefinition("a", default), Throws.TypeOf<ArgumentOutOfRangeException>());
			Assert.That(() => new IapProductDefinition("a", IapProductType.Consumable, " "), Throws.ArgumentException);
			Assert.That(() => new IapStoreOrder(Gem.Id, "", "r", true, null), Throws.ArgumentException);
			Assert.That(new IapProductDefinition("a", IapProductType.Consumable).StoreSpecificId, Is.EqualTo("a"));
		}

		private void CreateWith(Action<IapServiceOptions> configure)
		{
			Create(configure).Dispose();
		}
	}
}
