using System;
using Geuneda.Analytics;
using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.Iap.Analytics
{
	/// <summary>
	/// 스토어가 확정한 구매(<see cref="IIapService.PurchaseConfirmed"/>)를 analytics 의 구매 표준 이벤트(<c>LogPurchase</c>)로 기록합니다.
	/// 확정 성공은 거래마다 한 번이라 매출이 두 번 잡히지 않고, 지급에 실패해 열려 있는 거래는 잡히지 않습니다.
	/// </summary>
	/// <remarks>
	/// <para>com.geuneda.analytics 0.2.0 이상이 있으면 켜지는 선택 어셈블리입니다. 메인 스레드에서 만들고, 끝나면 <see cref="Dispose"/> 합니다.</para>
	/// <para>
	/// 금액은 실결제 금액이어야 합니다. 상품 조회 때의 표시 가격(<see cref="IapOrder.Price"/>)은 무료 체험·할인·프로모션 코드·가격 변경 전 결제와 다를 수 있습니다.
	/// 서버가 영수증 검증으로 확인한 금액과 거래 통화를 지급자 응답에 담아 <c>paidAmount</c> 로 돌려주세요(이전 실행의 결제를 복구하면 상품 조회 통화가 결제 때와 다를 수 있음).
	/// 기본값은 구독은 기록하지 않고(무료 체험·첫 달 할인이 흔함) 그 밖은 표시 가격과 상품 조회 통화를 씁니다. null 을 돌려주면 그 구매는 기록하지 않습니다.
	/// </para>
	/// <para>통화 코드를 모르면(상품 정보를 받지 못한 주문) 기록하지 않고 Warning 을 남깁니다.</para>
	/// </remarks>
	public sealed class IapRevenueLogger : IDisposable
	{
		private static readonly ModuleLogger Logger = new ModuleLogger("Geuneda.Iap.Analytics");

		private readonly IIapService _iap;
		private readonly IAnalyticsService _analytics;
		private readonly AnalyticsSinks? _sinks;
		private readonly Func<PurchaseSettlement, IapPaidAmount?> _paidAmount;
		private bool _disposed;

		/// <param name="iap">구매를 받을 서비스</param>
		/// <param name="analytics">기록할 서비스</param>
		/// <param name="sinks">보낼 sink. null 이면 analytics 의 기본 sink</param>
		/// <param name="paidAmount">실결제 금액과 통화. null 을 돌려주면 기록하지 않음. 생략하면 <see cref="DefaultPaidAmount"/></param>
		public IapRevenueLogger(IIapService iap, IAnalyticsService analytics, AnalyticsSinks? sinks = null,
			Func<PurchaseSettlement, IapPaidAmount?> paidAmount = null)
		{
			_iap = iap ?? throw new ArgumentNullException(nameof(iap));
			_analytics = analytics ?? throw new ArgumentNullException(nameof(analytics));
			_sinks = sinks;
			_paidAmount = paidAmount ?? DefaultPaidAmount;
			_iap.PurchaseConfirmed += OnPurchaseConfirmed;
		}

		/// <summary>구독은 null(기록 안 함), 그 밖은 상품 조회 때의 표시 가격과 통화입니다. 통화를 모르면 null 입니다.</summary>
		public static IapPaidAmount? DefaultPaidAmount(PurchaseSettlement settlement)
		{
			var order = settlement.Order;

			if (order.Definition?.Type == IapProductType.Subscription || order.CurrencyCode == null)
			{
				return null;
			}

			return new IapPaidAmount(order.Price, order.CurrencyCode);
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_iap.PurchaseConfirmed -= OnPurchaseConfirmed;
		}

		private void OnPurchaseConfirmed(PurchaseSettlement settlement)
		{
			var order = settlement.Order;
			var paid = _paidAmount(settlement);

			if (paid == null)
			{
				Logger.Info($"Revenue of {order} is not logged: the paid amount is unknown");
				return;
			}

			var amount = paid.Value;

			if (amount.Amount < 0 || !IsCurrencyCode(amount.CurrencyCode))
			{
				Logger.Warning($"Revenue of {order} is not logged: the paid amount '{amount}' is negative or has no currency code");
				return;
			}

			var title = string.IsNullOrEmpty(order.ProductTitle) ? null : order.ProductTitle;
			var purchase = new AnalyticsPurchase(order.ProductId, (double) amount.Amount, amount.CurrencyCode, 1, order.TransactionId, title);

			if (_sinks.HasValue)
			{
				_analytics.LogPurchase(purchase, null, _sinks.Value);
			}
			else
			{
				_analytics.LogPurchase(purchase);
			}
		}

		private static bool IsCurrencyCode(string code)
		{
			if (code == null || code.Length != 3)
			{
				return false;
			}

			foreach (var c in code)
			{
				if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z'))
				{
					return false;
				}
			}

			return true;
		}
	}
}
