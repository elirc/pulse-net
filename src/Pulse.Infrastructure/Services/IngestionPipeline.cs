using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

/// <summary>
/// Singleton wake-up signal between the capture endpoint and the background
/// worker: enqueue rings the bell, the worker drains the queue. The channel
/// carries no data — the queue table is the source of truth — so a missed
/// signal only delays processing until the worker's periodic sweep.
/// </summary>
public class IngestionSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Ring() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout: fall through to the periodic sweep.
        }
    }
}

/// <summary>Process-lifetime ingestion counters backing the metrics endpoint.</summary>
public class IngestionCounters
{
    private long _processed;
    private long _deadLettered;

    public long Processed => Interlocked.Read(ref _processed);

    public long DeadLettered => Interlocked.Read(ref _deadLettered);

    public void AddProcessed(int count) => Interlocked.Add(ref _processed, count);

    public void AddDeadLettered(int count) => Interlocked.Add(ref _deadLettered, count);
}
