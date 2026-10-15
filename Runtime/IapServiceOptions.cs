using System;
using System.Collections.Generic;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap
{
	/// <summary><see cref="IapService"/> 설정입니다.</summary>
	public sealed class IapServiceOptions
	{
		public static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);

		/// <summary>파는 상품 목록. 비거나 ID·스토어 ID 가 겹치면 서비스 생성이 실패합니다</summary>
		public IReadOnlyList<IapProductDefinition> Products { get; set; }

		public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

		/// <summary>상품·주문·보유 상태 조회 제한 시간</summary>
		public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromSeconds(15);

		/// <summary>
		/// 결제창(그리고 복원 요청)이 답할 때까지 기다리는 시간입니다. 넘으면 호출자는 <see cref="Geuneda.Core.ResultCodes.Timeout"/> 를 받지만,
		/// 늦게 온 결제도 서비스가 뒤에서 지급합니다. 카드 입력·본인 인증이 길 수 있어 넉넉히 둡니다
		/// </summary>
		public TimeSpan StoreResponseTimeout { get; set; } = TimeSpan.FromMinutes(3);

		/// <summary>지급자 한 번의 제한 시간. 넘으면 다시 시도할 주문으로 둡니다</summary>
		public TimeSpan FulfillTimeout { get; set; } = TimeSpan.FromSeconds(30);

		public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(15);

		/// <summary>다시 시도 첫 대기. 실패마다 두 배씩 <see cref="MaxRetryDelay"/> 까지 늘고 50~100% 무작위입니다</summary>
		public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

		public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

		/// <summary>앱이 포그라운드로 돌아올 때 스토어를 다시 조회해 미확정 주문을 처리할지</summary>
		public bool RecoverOnResume { get; set; } = true;
	}
}
