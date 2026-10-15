using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// Unity IAP 에디터 가짜 스토어(<see cref="IapStoreNames.Fake"/>)의 주문을 검증 없이 받습니다. 에디터에서 서버 지급까지 확인할 때 씁니다.
	/// 테스트 결제로 표시하고, 호스트 환경이 Production 이면 만들 때 실패합니다.
	/// </summary>
	internal sealed class FakeStoreVerifier : IStoreVerifier
	{
		private static readonly string[] StoreNames = { IapStoreNames.Fake };

		private readonly TimeProvider _time;

		public FakeStoreVerifier(IHostEnvironment environment, TimeProvider time)
		{
			if (environment.IsProduction())
			{
				throw new InvalidOperationException("The fake store accepts any order; do not add it in Production (AddFakeStore)");
			}

			_time = time;
		}

		public IReadOnlyCollection<string> Stores => StoreNames;

		public string LedgerStore => IapStoreNames.Fake;

		public Task<StoreVerification> VerifyAsync(IapRedeemRequest request, IapServerProduct product, CancellationToken cancellationToken)
		{
			return Task.FromResult(StoreVerification.Verified(new VerifiedPurchase
			{
				Store = IapStoreNames.Fake,
				TransactionId = request.TransactionId,
				StoreProductId = product.Id,
				ProductType = product.Type,
				PurchasedAt = _time.GetUtcNow(),
				SubscriptionState = product.Type == IapProductType.Subscription ? IapSubscriptionState.Active : null,
				IsTest = true
			}));
		}

		public Task<bool> AcknowledgeAsync(PurchaseRecord record, IapServerProduct product, CancellationToken cancellationToken)
		{
			return Task.FromResult(true);
		}
	}
}
