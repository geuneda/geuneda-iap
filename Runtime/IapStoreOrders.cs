using System;
using System.Collections.Generic;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary><see cref="IIapStore.FetchOrdersAsync"/> 의 결과입니다.</summary>
	public sealed class IapStoreOrders
	{
		/// <param name="pending">결제됐지만 확정하지 않은 주문</param>
		/// <param name="owned">이미 확정해 보유 중인 주문(비소모성·구독)</param>
		/// <param name="deferredCount">승인을 기다리는 결제 수(자녀 보호 승인, 현장 결제 등)</param>
		public IapStoreOrders(IReadOnlyList<IapStoreOrder> pending, IReadOnlyList<IapStoreOrder> owned, int deferredCount)
		{
			if (deferredCount < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(deferredCount), deferredCount, "Deferred count must not be negative");
			}

			Pending = pending ?? throw new ArgumentNullException(nameof(pending));
			Owned = owned ?? throw new ArgumentNullException(nameof(owned));
			DeferredCount = deferredCount;
		}

		public IReadOnlyList<IapStoreOrder> Pending { get; }

		public IReadOnlyList<IapStoreOrder> Owned { get; }

		public int DeferredCount { get; }
	}
}
