using System.Collections.Generic;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// App Store 검증 설정입니다. StoreKit 2 거래 서명(JWS)을 서버에서 검증하므로 App Store Server API 키가 필요 없습니다.
	/// 검증 규칙은 Apple 공식 App Store Server Library 의 <c>SignedDataVerifier</c> 와 같습니다(체인 3개, 인증서 OID, ES256 서명, 번들 ID, 환경).
	/// </summary>
	public sealed class AppStoreOptions
	{
		/// <summary>앱 번들 ID(필수). App Store·Mac App Store 거래 모두 이 값과 맞아야 합니다</summary>
		public string BundleId { get; set; }

		/// <summary>
		/// 샌드박스 거래를 받을지입니다. 기본 true 입니다. App Store 심사는 운영 서버에 샌드박스 결제를 보내므로, 끄면 심사에서 구매가 실패합니다.
		/// 샌드박스 거래는 <see cref="VerifiedPurchase.IsTest"/> 가 true 입니다
		/// </summary>
		public bool AllowSandbox { get; set; } = true;

		/// <summary>
		/// 온라인 검사(인증서 폐기 OCSP 확인, 지금 시각 기준 유효 기간)를 할지입니다. 기본 true(Apple 권장)입니다.
		/// 끄면 서명 시각 기준으로 체인을 확인하고 폐기는 보지 않습니다(오래된 데이터를 다시 검증할 때). OCSP 에 닿지 못하면 다시 시도하게 합니다
		/// </summary>
		public bool OnlineChecks { get; set; } = true;

		/// <summary>신뢰할 루트 인증서(DER). 비어 있으면 <see cref="AppleRootCertificates.AppleRootCaG3"/> 하나입니다. 테스트에서는 자체 루트를 넣습니다</summary>
		public List<byte[]> RootCertificates { get; } = new List<byte[]>();
	}
}
