using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Geuneda.Iap.Server;
using Microsoft.Extensions.Logging;

namespace Geuneda.Iap.SampleServer
{
	/// <summary>계정별 보석 잔액(메모리). 지급자와 알림 처리기가 함께 씁니다. 실제 게임은 원장과 같은 DB 트랜잭션에서 인벤토리를 씁니다.</summary>
	public sealed class SampleBalances
	{
		private readonly ConcurrentDictionary<string, int> _gems = new ConcurrentDictionary<string, int>();

		public int Add(string accountId, int gems)
		{
			return _gems.AddOrUpdate(accountId, gems, (_, current) => current + gems);
		}
	}

	/// <summary>보석 100 개 상품을 지급하는 샘플 지급자입니다(<see cref="IPurchaseLedger"/> 참고).</summary>
	public sealed class SampleWallet : IPurchaseGrantor
	{
		private readonly SampleBalances _balances;

		public SampleWallet(SampleBalances balances)
		{
			_balances = balances;
		}

		public Task<GrantResult> GrantAsync(PurchaseGrant grant, CancellationToken cancellationToken)
		{
			var added = grant.Product.Id == "gem_100" ? 100 * grant.Quantity : 0;
			var balance = _balances.Add(grant.AccountId, added);
			return Task.FromResult(GrantResult.Granted(new SampleGrant { Product = grant.Product.Id, Gems = added, Balance = balance }));
		}
	}

	/// <summary>서버 알림 샘플 처리기입니다. 환불이면 보석을 회수하고(잔액이 음수가 될 수 있음), 구독 변경은 로그만 남깁니다.</summary>
	public sealed class SampleNotifications : IPurchaseNotificationHandler
	{
		private readonly SampleBalances _balances;
		private readonly ILogger<SampleNotifications> _logger;

		public SampleNotifications(SampleBalances balances, ILogger<SampleNotifications> logger)
		{
			_balances = balances;
			_logger = logger;
		}

		public Task OnRefundedAsync(PurchaseRefund refund, CancellationToken cancellationToken)
		{
			if (refund.Product?.Id == "gem_100")
			{
				_balances.Add(refund.AccountId, -100 * refund.Quantity);
			}

			return Task.CompletedTask;
		}

		public Task OnRefundReversedAsync(PurchaseRefund refund, CancellationToken cancellationToken)
		{
			if (refund.Product?.Id == "gem_100")
			{
				_balances.Add(refund.AccountId, 100 * refund.Quantity);
			}

			return Task.CompletedTask;
		}

		public Task OnSubscriptionChangedAsync(SubscriptionUpdate update, CancellationToken cancellationToken)
		{
			_logger.LogInformation("Subscription of account {AccountId}: {Update}", update.AccountId, update);
			return Task.CompletedTask;
		}
	}

	/// <summary>샘플 지급 응답입니다. Unity 상호 운용 테스트가 같은 모양으로 읽습니다.</summary>
	public sealed class SampleGrant
	{
		public string Product { get; set; }
		public int Gems { get; set; }
		public int Balance { get; set; }
	}
}
