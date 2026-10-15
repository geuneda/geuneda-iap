using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 구독 지급을 원장의 구독 스냅숏에 합칩니다. 지급 콜백(지급 트랜잭션) 안에서 불려, 합치지 못하면 지급과 함께 롤백되고 클라이언트가 다시 보냅니다(따로 재처리하지 않아도 누락이 없음).
	/// 알림 처리기가 없으면(서버 알림을 쓰지 않음) 하지 않습니다. 요청 범위입니다.
	/// </summary>
	/// <remarks>
	/// Google 은 구독 잠금 안에서 지금 상태를 다시 읽습니다. 그 밖의 스토어(App Store, 가짜 스토어, 사용자 정의 스토어)는 지급한 거래의 상태로 합칩니다(<see cref="SubscriptionMerge.ByPeriod"/>).
	/// 그 규칙은 거래의 구독 상태와 구매 시각(기간)이 있어야 하므로, 없으면(검증기 오용) 지급 전에 숨기지 않고 예외를 던집니다(<see cref="EnsureMergeable"/>).
	/// </remarks>
	internal sealed class SubscriptionSync
	{
		/// <summary>구독 지급이 만든 스냅숏의 <see cref="SubscriptionUpdate.NotificationType"/></summary>
		internal const string Redeem = "REDEEM";

		private readonly IServiceProvider _services;
		private readonly IPurchaseLedger _ledger;

		public SubscriptionSync(IServiceProvider services, IPurchaseLedger ledger)
		{
			_services = services;
			_ledger = ledger;
		}

		/// <summary>
		/// 지급 전에(지급자를 부르기 전에) 이 구독 거래를 합칠 수 있는지 확인합니다. Google 이 아닌 스토어는 거래의 구독 상태와 구매 시각이 있어야 기간 순서를 정할 수 있습니다
		/// (구매 시각이 없으면 모든 거래가 같은 기간이 되어 갱신이 버려짐, 받은 시각으로 대신하면 옛 거래 재전달이 최신 기간이 됨).
		/// </summary>
		/// <exception cref="InvalidOperationException">검증기가 구독 거래에 <see cref="VerifiedPurchase.SubscriptionState"/> 나 <see cref="VerifiedPurchase.PurchasedAt"/> 를 채우지 않음</exception>
		public void EnsureMergeable(VerifiedPurchase purchase)
		{
			if (purchase.SubscriptionId == null || purchase.Store == IapStoreNames.GooglePlay || _services.GetService<IPurchaseNotificationHandler>() == null)
			{
				return;
			}

			if (purchase.SubscriptionState == null || purchase.PurchasedAt == null)
			{
				throw new InvalidOperationException($"Verified subscription {purchase} needs SubscriptionState and PurchasedAt to merge its entitlement");
			}
		}

		/// <returns>합쳤거나 할 일이 없으면 true, 스토어에서 지금 상태를 읽지 못했으면 false(지급을 롤백하고 다시 시도)</returns>
		public async Task<bool> SyncGrantAsync(string accountId, VerifiedPurchase purchase, IapServerProduct product, CancellationToken cancellationToken)
		{
			var handler = _services.GetService<IPurchaseNotificationHandler>();

			if (purchase.SubscriptionId == null || handler == null)
			{
				return true;
			}

			if (purchase.Store == IapStoreNames.GooglePlay)
			{
				var refunds = ActivatorUtilities.CreateInstance<RefundApplier>(_services);
				return await ActivatorUtilities.CreateInstance<GooglePlayNotificationProcessor>(_services, refunds)
					.SyncGrantAsync(accountId, purchase.TransactionId, cancellationToken);
			}

			// 거래 서명만으로 만든 상태라 관측 시각은 그 거래의 구매 시각이다(같은 기간의 알림은 그 뒤에 서명되므로 언제 오든 이김)
			var update = new SubscriptionUpdate
			{
				Store = purchase.Store,
				SubscriptionId = purchase.SubscriptionId,
				StoreProductId = purchase.StoreProductId,
				Product = product,
				AccountId = accountId,
				State = purchase.SubscriptionState.Value,
				ExpiresAt = purchase.ExpiresAt,
				IsTest = purchase.IsTest,
				NotificationType = Redeem,
				ObservedAt = purchase.PurchasedAt.Value,
				PeriodStartedAt = purchase.PurchasedAt,
				AccountPeriodStartedAt = purchase.PurchasedAt,
				LatestTransactionId = purchase.TransactionId
			};
			await _ledger.UpdateSubscriptionAsync(purchase.Store, purchase.SubscriptionId,
				(stored, _) => Task.FromResult(SubscriptionMerge.ByPeriod(stored, update, true)), handler.OnSubscriptionChangedAsync, cancellationToken);
			return true;
		}
	}
}
