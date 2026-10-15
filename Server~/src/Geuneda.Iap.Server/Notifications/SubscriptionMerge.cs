using System;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 거래마다 결제 기간이 있는 스토어(App Store, 가짜 스토어, 사용자 정의 스토어)의 구독 스냅숏을 합치는 규칙입니다(원장의 구독 잠금 안에서 부름). 기간(<see cref="SubscriptionUpdate.PeriodStartedAt"/>,
	/// 거래 구매 시각)이 뒤인 것이 이기고, 같은 기간이면 늦게 서명된 알림이 이깁니다. 만료 시각은 기준으로 쓰지 않습니다(긴 상품에서 짧은 상품으로 업그레이드하면 새 거래의 만료가 더 이름).
	/// 소유 계정은 스냅숏이 정합니다. 지급한 거래가 소유자를 정한 지급(<see cref="SubscriptionUpdate.AccountPeriodStartedAt"/>)보다 같거나 뒤 기간이면 그 계정이고, 알림은 소유자를 바꾸지 않습니다.
	/// </summary>
	internal static class SubscriptionMerge
	{
		/// <param name="stored">저장된 스냅숏. 없으면 null</param>
		/// <param name="candidate">새 정보(계정은 호출자가 정함)</param>
		/// <param name="transactionOnly">지급한 거래 서명만으로 만든 상태인지(자동 갱신·유예 정보 없음, 관측 시각은 그 거래의 구매 시각)</param>
		/// <returns>
		/// 합친 스냅숏. 알림이 지면(앞 기간이거나 같은 기간의 이른 알림) null 이고 아무것도 바꾸지 않습니다(버린 알림이 순서 기준을 올리지 않게).
		/// 지급한 거래는 뒤 기간이면 상태도 바꾸고, 아니면 소유자가 될 때만 저장된 상태에 그 계정을 연결합니다(아무것도 바뀌지 않으면 null)
		/// </returns>
		public static SubscriptionUpdate ByPeriod(SubscriptionUpdate stored, SubscriptionUpdate candidate, bool transactionOnly)
		{
			if (stored == null)
			{
				return candidate;
			}

			var statePeriod = Nullable.Compare(candidate.PeriodStartedAt, stored.PeriodStartedAt);

			if (!transactionOnly)
			{
				// 알림은 상태만 바꾼다(소유자는 호출자가 저장된 것으로 넣음)
				return statePeriod > 0 || statePeriod == 0 && candidate.ObservedAt >= stored.ObservedAt ? candidate : null;
			}

			// 지급한 거래: 소유자를 정한 지급보다 같거나 뒤 기간이면 소유자가 된다(알림이 상태 기간을 먼저 올렸어도). 상태는 뒤 기간일 때만 바꾼다
			var takesOwner = stored.AccountId == null || Nullable.Compare(candidate.PeriodStartedAt, stored.AccountPeriodStartedAt) >= 0;

			if (!takesOwner && statePeriod <= 0)
			{
				return null;
			}

			// 거래 서명에는 자동 갱신 정보가 없어 앞 스냅숏의 것을 두고, 유예는 지난 기간의 것이라 버린다. 상태를 두면 순서 기준(관측 시각, 기간)과 유예도 그대로다
			var state = statePeriod > 0 ? candidate with { AutoRenewing = stored.AutoRenewing } : stored;
			return state with
			{
				AccountId = takesOwner ? candidate.AccountId : stored.AccountId,
				AccountPeriodStartedAt = takesOwner ? candidate.AccountPeriodStartedAt : stored.AccountPeriodStartedAt,
				NotificationType = candidate.NotificationType
			};
		}
	}
}
