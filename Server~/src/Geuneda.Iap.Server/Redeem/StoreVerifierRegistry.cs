using System;
using System.Collections.Generic;
using System.Linq;

namespace Geuneda.Iap.Server
{
	/// <summary>스토어 이름으로 검증기를 찾습니다. 두 검증기가 같은 스토어를 받으면 만들 때 실패합니다.</summary>
	internal sealed class StoreVerifierRegistry
	{
		private readonly Dictionary<string, IStoreVerifier> _verifiers = new Dictionary<string, IStoreVerifier>(StringComparer.Ordinal);

		public StoreVerifierRegistry(IEnumerable<IStoreVerifier> verifiers)
		{
			foreach (var verifier in verifiers)
			{
				if (!verifier.Stores.Contains(verifier.LedgerStore))
				{
					throw new InvalidOperationException($"{verifier.GetType().Name}.LedgerStore {verifier.LedgerStore} is not one of its Stores");
				}

				foreach (var store in verifier.Stores)
				{
					if (!_verifiers.TryAdd(store, verifier))
					{
						throw new InvalidOperationException($"Two store verifiers handle {store}: {_verifiers[store].GetType().Name}, {verifier.GetType().Name}");
					}
				}
			}
		}

		public int Count => _verifiers.Count;

		public bool TryGet(string store, out IStoreVerifier verifier)
		{
			return _verifiers.TryGetValue(store, out verifier);
		}
	}
}
