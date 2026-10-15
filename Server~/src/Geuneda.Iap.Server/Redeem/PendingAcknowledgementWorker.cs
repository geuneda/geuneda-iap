using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geuneda.Iap.Server
{
	/// <summary>
	/// 지급은 기록했지만 스토어 확인(Google acknowledge)을 못 한 거래를 다시 확인합니다. 시작할 때와 <see cref="IapServerOptions.AcknowledgementRetryInterval"/> 마다
	/// 원장에서 대기 기록을 읽습니다. 확인 직후 실패했거나 그 사이 서버가 죽었어도, 클라이언트가 돌아오지 않는 결제가 자동 환불되지 않게 합니다.
	/// </summary>
	internal sealed class PendingAcknowledgementWorker : BackgroundService
	{
		internal const int BatchSize = 100;

		/// <summary>한 회차의 최대 배치 수. 원장 구현이 미루기를 반영하지 못해도 회차가 끝나게 한다(남은 것은 다음 회차)</summary>
		internal const int MaxBatchesPerPass = 100;

		private readonly IServiceScopeFactory _scopes;
		private readonly StoreVerifierRegistry _verifiers;
		private readonly IapProductCatalog _catalog;
		private readonly TimeSpan _interval;
		private readonly TimeProvider _time;
		private readonly ILogger<PendingAcknowledgementWorker> _logger;

		public PendingAcknowledgementWorker(IServiceScopeFactory scopes, StoreVerifierRegistry verifiers, IapProductCatalog catalog,
			IOptions<IapServerOptions> options, TimeProvider time, ILogger<PendingAcknowledgementWorker> logger)
		{
			_scopes = scopes;
			_verifiers = verifiers;
			_catalog = catalog;
			_interval = options.Value.AcknowledgementRetryInterval;
			_time = time;
			_logger = logger;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					await RunOnceAsync(stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					return;
				}
				catch (Exception e)
				{
					// 원장(DB) 장애 등으로 한 회차가 실패해도 호스트를 멈추지 않는다(BackgroundService 예외는 기본으로 호스트를 끔). 숨기지 않고 Error 로 남기고 다음 회차에 다시 한다
					_logger.LogError(e, "Retrying pending store acknowledgements failed; trying again in {Interval}", _interval);
				}

				try
				{
					await Task.Delay(_interval, _time, stoppingToken);
				}
				catch (OperationCanceledException)
				{
					return;
				}
			}
		}

		/// <summary>
		/// 시각이 된 대기 기록을 모두 처리하고, 확인을 끝낸 수를 돌려줍니다. 확인하지 못한 기록은 다음 간격 뒤로 미뤄, 앞의 실패가 뒤의 기록을 막지 않게 다음 배치로 넘어갑니다.
		/// </summary>
		internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
		{
			using (var scope = _scopes.CreateScope())
			{
				var ledger = scope.ServiceProvider.GetRequiredService<IPurchaseLedger>();
				var now = _time.GetUtcNow();
				var retryAt = now + _interval;
				var settled = 0;

				for (var batch = 0; batch < MaxBatchesPerPass; batch++)
				{
					var records = await ledger.FindDueAcknowledgementsAsync(now, BatchSize, cancellationToken);

					foreach (var record in records)
					{
						if (await AcknowledgeAsync(record, cancellationToken))
						{
							await ledger.MarkAcknowledgedAsync(record.Store, record.TransactionId, cancellationToken);
							settled++;
						}
						else
						{
							await ledger.DeferAcknowledgementAsync(record.Store, record.TransactionId, retryAt, cancellationToken);
						}
					}

					if (records.Count < BatchSize)
					{
						break;
					}
				}

				return settled;
			}
		}

		private async Task<bool> AcknowledgeAsync(PurchaseRecord record, CancellationToken cancellationToken)
		{
			if (!_verifiers.TryGet(record.Store, out var verifier) || !_catalog.TryGet(record.ProductId, out var product))
			{
				_logger.LogError("Cannot acknowledge {Store} transaction {TransactionId}: no verifier or product {ProductId} (do not remove sold products)",
					record.Store, record.TransactionId, record.ProductId);
				return false;
			}

			return await verifier.AcknowledgeAsync(record, product, cancellationToken);
		}
	}
}
