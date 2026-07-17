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

/// <summary>
/// Drains the queue table: deserializes each payload, re-validates it, and
/// pushes it through the capture pipeline in enqueue order. Permanent
/// failures (bad JSON, failed validation, vanished project) dead-letter
/// immediately; transient failures retry up to <see cref="MaxAttempts"/>.
/// </summary>
public class IngestionProcessor
{
    public const int MaxAttempts = 3;
    public const int BatchSize = 200;

    private readonly PulseDbContext _db;
    private readonly CaptureService _capture;
    private readonly IngestionCounters _counters;

    public IngestionProcessor(PulseDbContext db, CaptureService capture, IngestionCounters counters)
    {
        _db = db;
        _capture = capture;
        _counters = counters;
    }

    /// <summary>Processes up to one batch. Returns how many rows left the queue.</summary>
    public async Task<(int Processed, int DeadLettered)> ProcessPendingAsync(CancellationToken ct = default)
    {
        var batch = await _db.QueuedEvents
            .AsNoTracking()
            .OrderBy(q => q.Seq)
            .Take(BatchSize)
            .ToListAsync(ct);

        var processed = 0;
        var deadLettered = 0;
        var projects = new Dictionary<Guid, Project?>();

        foreach (var row in batch)
        {
            if (!projects.TryGetValue(row.ProjectId, out var project))
            {
                project = await _db.Projects.FindAsync([row.ProjectId], ct);
                projects[row.ProjectId] = project;
            }

            var (outcome, error) = await ProcessRowAsync(row, project, ct);
            switch (outcome)
            {
                case RowOutcome.Processed:
                    processed++;
                    await DeleteRowAsync(row.Seq, ct);
                    break;

                case RowOutcome.DeadLettered:
                    deadLettered++;
                    _db.DeadLetterEvents.Add(new DeadLetterEvent
                    {
                        ProjectId = row.ProjectId,
                        PayloadJson = row.PayloadJson,
                        Error = error!,
                        Attempts = row.Attempts,
                    });
                    await _db.SaveChangesAsync(ct);
                    await DeleteRowAsync(row.Seq, ct);
                    break;

                case RowOutcome.Retry:
                    await _db.QueuedEvents
                        .Where(q => q.Seq == row.Seq)
                        .ExecuteUpdateAsync(s => s.SetProperty(q => q.Attempts, row.Attempts + 1), ct);
                    break;
            }
        }

        _counters.AddProcessed(processed);
        _counters.AddDeadLettered(deadLettered);
        return (processed, deadLettered);
    }

    private Task<int> DeleteRowAsync(long seq, CancellationToken ct) =>
        _db.QueuedEvents.Where(q => q.Seq == seq).ExecuteDeleteAsync(ct);

    private enum RowOutcome
    {
        Processed,
        DeadLettered,
        Retry,
    }

    private async Task<(RowOutcome Outcome, string? Error)> ProcessRowAsync(
        QueuedEvent row,
        Project? project,
        CancellationToken ct)
    {
        if (project is null)
        {
            return (RowOutcome.DeadLettered, "Project no longer exists.");
        }

        IncomingEvent? incoming;
        try
        {
            incoming = JsonSerializer.Deserialize<IncomingEvent>(row.PayloadJson);
        }
        catch (JsonException)
        {
            incoming = null;
        }

        if (incoming is null
            || string.IsNullOrWhiteSpace(incoming.Name)
            || string.IsNullOrWhiteSpace(incoming.DistinctId))
        {
            return (RowOutcome.DeadLettered, "Payload failed validation: event and distinct_id are required.");
        }

        try
        {
            await _capture.IngestAsync(project, [incoming], ct);
            return (RowOutcome.Processed, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed ingest can leave half-tracked entities behind; drop
            // them so they don't leak into the next row's SaveChanges.
            _db.ChangeTracker.Clear();

            return row.Attempts + 1 >= MaxAttempts
                ? (RowOutcome.DeadLettered, $"Failed after {MaxAttempts} attempts: {ex.Message}")
                : (RowOutcome.Retry, null);
        }
    }
}
