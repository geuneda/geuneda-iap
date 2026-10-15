using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 스토어 하나(또는 같은 방식으로 검증하는 여러 스토어)의 영수증 검증기입니다. Google Play, App Store, 가짜 스토어 검증기가 들어 있고,
	/// 다른 스토어(원스토어 등)는 이것을 구현해 <see cref="IapServerBuilder.AddVerifier{T}"/> 로 붙입니다.
	/// </summary>
	/// <remarks>
	/// 정상 실패(네트워크, 스토어 장애, 잘못된 영수증)는 예외 대신 <see cref="StoreVerification.Failed"/> 로 돌려줍니다.
	/// 확실하지 않으면 <see cref="StoreVerificationStatus.Unavailable"/> 를 돌려주세요(클라이언트가 거래를 열어 두고 다시 보냄). 잘못 거절하거나 닫으면 지급이 누락됩니다.
	/// </remarks>
	public interface IStoreVerifier
	{
		/// <summary>이 검증기가 받는 스토어 이름(<see cref="IapStoreNames"/>)</summary>
		IReadOnlyCollection<string> Stores { get; }

		/// <summary>
		/// 원장 키(<see cref="PurchaseRecord.Store"/>)와 <see cref="VerifiedPurchase.Store"/> 에 쓰는 스토어 이름입니다. 거래 ID 공간을 같이 쓰는 스토어(App Store 와 Mac App Store)는
		/// 하나로 맞춰, 같은 거래가 다른 스토어 이름으로 와도 한 번만 지급합니다.
		/// </summary>
		string LedgerStore { get; }

		/// <summary>요청의 거래를 스토어에 확인합니다. <paramref name="product"/> 는 요청한 상품이고, 거래의 상품과 다르면 <see cref="StoreVerificationStatus.ProductMismatch"/> 입니다.</summary>
		Task<StoreVerification> VerifyAsync(IapRedeemRequest request, IapServerProduct product, CancellationToken cancellationToken);

		/// <summary>
		/// 지급을 기록한 거래를 스토어에 확인합니다(Google acknowledge). <see cref="VerifiedPurchase.RequiresAcknowledgement"/> 가 true 였던 기록에만, 커밋 직후와
		/// 확인 작업자의 재시도에서 부릅니다. 더 할 것이 없으면(확인함, 이미 확인됨, 취소됨, 확인 기한이 지남) true, 나중에 다시 해야 하면 false 를 돌려줍니다.
		/// 정상 실패는 예외 대신 false 이고, 지급은 이미 끝났으므로 로그만 남깁니다. 호출부는 요청 토큰을 넘기지 않습니다(요청이 끊겨도 끝까지 함).
		/// </summary>
		Task<bool> AcknowledgeAsync(PurchaseRecord record, IapServerProduct product, CancellationToken cancellationToken);
	}
}
