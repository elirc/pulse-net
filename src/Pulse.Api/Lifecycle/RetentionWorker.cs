using Pulse.Infrastructure.Services;

namespace Pulse.Api.Lifecycle;

public sealed class RetentionWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                if (!configuration.GetValue("Retention:WorkerEnabled", true)) continue;
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<EventRetentionService>().SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Retention cycle failed; committed batches remain recorded."); }
        }
    }
}
