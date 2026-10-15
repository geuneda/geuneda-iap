using System;
using System.Collections.Generic;

namespace Geuneda.Iap.Server
{
	/// <summary>결제 서버 설정입니다. 시작할 때 검사합니다(상품이 없거나 ID 가 겹치면 실패).</summary>
	public sealed class IapServerOptions
	{
		/// <summary>지급하는 상품 목록(필수). 클라이언트 정의와 같은 게임 상품 ID 를 씁니다</summary>
		public List<IapServerProduct> Products { get; } = new List<IapServerProduct>();

		/// <summary>
		/// 테스트 결제(Google 라이선스 테스터, App Store 샌드박스, 가짜 스토어)를 지급할지입니다. 기본 true 입니다.
		/// App Store 심사는 운영 서버에 샌드박스 결제를 보내므로, 끄면 심사에서 구매가 실패합니다. 매출 집계에서는 <see cref="VerifiedPurchase.IsTest"/> 로 빼세요.
		/// </summary>
		public bool AllowTestPurchases { get; set; } = true;

		/// <summary>지급 요청 본문의 최대 크기(바이트). 기본 1MB 입니다(App Store 앱 영수증이 함께 올 수 있음)</summary>
		public int MaxRequestBytes { get; set; } = 1024 * 1024;

		/// <summary>지급 뒤 스토어 확인(Google acknowledge)을 못 한 거래를 다시 확인하는 간격. 기본 5분이고, 서버가 시작할 때도 한 번 합니다</summary>
		public TimeSpan AcknowledgementRetryInterval { get; set; } = TimeSpan.FromMinutes(5);
	}
}
