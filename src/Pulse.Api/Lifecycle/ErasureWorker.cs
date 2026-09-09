using Pulse.Infrastructure.Services;

namespace Pulse.Api.Lifecycle;

public sealed class ErasureWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ErasureWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                if (!configuration.GetValue("Erasure:WorkerEnabled", true)) continue;
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PersonErasureService>().SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Erasure cycle stopped; inspect the durable job state before resuming."); }
        }
    }
}
