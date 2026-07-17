using Pulse.Infrastructure.Services;

namespace Pulse.Api.Export;

/// <summary>
/// Background consumer of export jobs: wakes when a job is created (or every
/// second as a safety sweep) and runs pending jobs to completion.
/// </summary>
public class ExportWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ExportSignal _signal;
    private readonly ILogger<ExportWorker> _logger;

    public ExportWorker(
        IServiceScopeFactory scopes,
        ExportSignal signal,
        ILogger<ExportWorker> logger)
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

                using var scope = _scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ExportJobProcessor>();
                await processor.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Export worker cycle failed; will retry.");
            }
        }
    }
}
