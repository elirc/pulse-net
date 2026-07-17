using Pulse.Infrastructure.Services;

namespace Pulse.Api.Ingestion;

/// <summary>
/// Background consumer of the ingestion queue. Wakes on the enqueue signal
/// (or every second as a safety sweep) and processes batches until the queue
/// is empty.
/// </summary>
public class IngestionWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IngestionSignal _signal;
    private readonly ILogger<IngestionWorker> _logger;

    public IngestionWorker(
        IServiceScopeFactory scopes,
        IngestionSignal signal,
        ILogger<IngestionWorker> logger)
    {
        _scopes = scopes;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(SweepInterval, stoppingToken);
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ingestion worker cycle failed; will retry.");
            }
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = _scopes.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IngestionProcessor>();

            var (processed, deadLettered) = await processor.ProcessPendingAsync(ct);
            if (processed + deadLettered == 0)
            {
                break; // Queue drained.
            }
        }
    }
}
