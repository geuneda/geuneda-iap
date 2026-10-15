using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>지급 결과를 대본대로 돌려주는 지급자입니다. 대본이 비면 <c>{ gems, product }</c> 를 지급합니다.</summary>
	internal sealed class TestGrantor : IPurchaseGrantor
	{
		private int _calls;

		public ConcurrentQueue<Func<PurchaseGrant, CancellationToken, Task<GrantResult>>> Script { get; } =
			new ConcurrentQueue<Func<PurchaseGrant, CancellationToken, Task<GrantResult>>>();

		public ConcurrentQueue<PurchaseGrant> Grants { get; } = new ConcurrentQueue<PurchaseGrant>();

		public int Calls => Volatile.Read(ref _calls);

		public void Enqueue(GrantResult result)
		{
			Script.Enqueue((_, _) => Task.FromResult(result));
		}

		public void Enqueue(Func<PurchaseGrant, CancellationToken, Task<GrantResult>> step)
		{
			Script.Enqueue(step);
		}

		public Task<GrantResult> GrantAsync(PurchaseGrant grant, CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _calls);
			Grants.Enqueue(grant);

			return Script.TryDequeue(out var step)
				? step(grant, cancellationToken)
				: Task.FromResult(GrantResult.Granted(new TestReward { Gems = 100 * grant.Quantity, Product = grant.Product.Id }));
		}
	}

	internal sealed class TestReward
	{
		public int Gems { get; set; }
		public string Product { get; set; }
	}
}
