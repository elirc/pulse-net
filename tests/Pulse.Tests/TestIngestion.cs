using System.Net.Http.Json;
using System.Text.Json;

namespace Pulse.Tests;

/// <summary>
/// Ingestion is asynchronous (capture returns 202 before events are
/// persisted); tests wait for the background worker to drain the queue
/// before querying.
/// </summary>
public static class TestIngestion
{
    public static async Task WaitForDrainAsync(HttpClient client, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));

        while (DateTimeOffset.UtcNow < deadline)
        {
            var metrics = await client.GetFromJsonAsync<JsonElement>("/api/ingestion/metrics");
            if (metrics.GetProperty("pending").GetInt32() == 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Ingestion queue did not drain in time.");
    }
}
