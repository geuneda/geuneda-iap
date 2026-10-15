using System;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// Google Play 검증 설정입니다. 서비스 계정은 Play Console 의 사용자·권한에서 앱에 초대하고 '주문 및 구독 관리' 권한을 줍니다(<see cref="ReadOrderTotal"/> 는 '재무 데이터 보기'도).
	/// 자격 증명은 <see cref="ServiceAccountKeyJson"/>, <see cref="ServiceAccountKeyFile"/>, 둘 다 없으면 Application Default Credentials(Cloud Run·GKE 의 연결된 서비스 계정 등) 순입니다.
	/// </summary>
	public sealed class GooglePlayOptions
	{
		/// <summary>앱 패키지 이름(필수, 예: <c>com.company.game</c>)</summary>
		public string PackageName { get; set; }

		/// <summary>서비스 계정 키 JSON 내용. 비밀 저장소에서 읽어 넣습니다. 없으면 null</summary>
		public string ServiceAccountKeyJson { get; set; }

		/// <summary>서비스 계정 키 JSON 파일 경로. 없으면 null</summary>
		public string ServiceAccountKeyFile { get; set; }

		/// <summary>
		/// 지급을 기록한 뒤 서버가 결제를 확인(acknowledge)할지입니다. 기본 true 입니다.
		/// Google 은 3일 안에 확인하지 않은 결제를 자동 환불하는데, 클라이언트가 확정하기 전에 앱이 지워지거나 다시 열리지 않으면 지급한 결제가 환불됩니다.
		/// 소모성도 확인만 하고 소비는 클라이언트 확정에 맡깁니다(서버가 소비하면 클라이언트 확정이 '구매 없음'으로 실패함).
		/// </summary>
		public bool AcknowledgeAfterGrant { get; set; } = true;

		/// <summary>
		/// 주문 API(<c>orders.get</c>)로 실결제 금액·통화를 읽어 <see cref="VerifiedPurchase.PaidAmount"/> 에 넣을지입니다. 기본 false 입니다.
		/// 서비스 계정에 '재무 데이터 보기' 권한이 필요하고 요청이 하나 늘어납니다. 실패해도 지급은 하고 금액만 비웁니다
		/// </summary>
		public bool ReadOrderTotal { get; set; }

		/// <summary>Google API 요청 하나의 제한 시간. 기본 10초입니다</summary>
		public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

		/// <summary>Android Publisher API 주소. 테스트에서만 바꿉니다</summary>
		public Uri ApiBaseAddress { get; set; } = new Uri("https://androidpublisher.googleapis.com/androidpublisher/v3/");

		/// <summary>
		/// 실시간 개발자 알림(RTDN) Pub/Sub 푸시 구독의 인증 대상(audience). 푸시 구독의 '인증 사용 설정'에서 정한 값(보통 푸시 엔드포인트 주소)입니다.
		/// <c>MapGeunedaIapGooglePlayNotifications</c> 를 쓰면 필수입니다
		/// </summary>
		public string NotificationAudience { get; set; }

		/// <summary>Pub/Sub 푸시 구독이 토큰에 서명하는 서비스 계정 이메일. <c>MapGeunedaIapGooglePlayNotifications</c> 를 쓰면 필수입니다</summary>
		public string NotificationServiceAccountEmail { get; set; }

		/// <summary>푸시 토큰 서명 키(JWKS) 주소. null 이면 Google 기본값입니다. 테스트에서만 바꿉니다</summary>
		public Uri NotificationCertificatesUrl { get; set; }
	}
}
