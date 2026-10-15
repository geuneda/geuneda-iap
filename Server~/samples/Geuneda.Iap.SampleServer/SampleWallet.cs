using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Geuneda.Iap.Server;

namespace Geuneda.Iap.SampleServer
{
	/// <summary>
	/// 계정별 보석 잔액을 메모리에 두는 샘플 지급자입니다. 실제 게임은 원장과 같은 DB 트랜잭션에서 인벤토리를 씁니다(<see cref="IPurchaseLedger"/> 참고).
	/// </summary>
	public sealed class SampleWallet : IPurchaseGrantor
	{
		private readonly ConcurrentDictionary<string, int> _gems = new ConcurrentDictionary<string, int>();

		public Task<GrantResult> GrantAsync(PurchaseGrant grant, CancellationToken cancellationToken)
		{
			var added = grant.Product.Id == "gem_100" ? 100 * grant.Purchase.Quantity : 0;
			var balance = _gems.AddOrUpdate(grant.AccountId, added, (_, current) => current + added);
			return Task.FromResult(GrantResult.Granted(new SampleGrant { Product = grant.Product.Id, Gems = added, Balance = balance }));
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
