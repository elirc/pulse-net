using Pulse.Infrastructure.Services;

namespace Pulse.Api.Scheduling;

/// <summary>Periodically asks the durable alert processor for one bounded cycle.</summary>
public sealed class HourlyAlertWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<HourlyAlertWorker> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (configuration.GetValue("HourlyAlerts:Enabled", true))
                {
                    using var scope = scopes.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<HourlyAlertService>();
                    var processed = await service.ProcessDueAsync(stoppingToken);
                    if (processed > 0)
                    {
                        logger.LogInformation("Processed {WindowCount} hourly alert windows.", processed);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Hourly alert worker cycle failed; persisted progress will be retried.");
            }

            var seconds = Math.Clamp(configuration.GetValue("HourlyAlerts:SweepSeconds", 60), 1, 3600);
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }
}
