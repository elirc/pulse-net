using Pulse.Infrastructure.Services;

namespace Pulse.Api.Scheduling;

public sealed class FlagScheduleWorker(IServiceScopeFactory scopes, FlagScheduleSignal signal,
    IConfiguration configuration, ILogger<FlagScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("FlagScheduling:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<FlagScheduleService>().ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Flag schedule sweep failed; pending schedules remain retryable."); }
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wake.CancelAfter(TimeSpan.FromSeconds(5));
            try { await signal.WaitAsync(wake.Token); }
            catch (OperationCanceledException) when (wake.IsCancellationRequested) { }
        }
    }
}
