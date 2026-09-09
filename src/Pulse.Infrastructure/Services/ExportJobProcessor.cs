using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public sealed class MissingExportInsightException() : Exception("No such insight in this project.");

/// <summary>Wake-up signal for the export worker (same pattern as ingestion).</summary>
public class ExportSignal
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
            // Timeout: periodic sweep.
        }
    }
}

/// <summary>
/// Executes pending export jobs: pages through the full (capped) dataset,
/// renders it in the requested format, and stores the finished document on
/// the job row for download.
/// </summary>
public class ExportJobProcessor
{
    /// <summary>Safety cap on rows per async export.</summary>
    public const int MaxRows = 50_000;

    private const int PageSize = 1000;

    private readonly PulseDbContext _db;
    private readonly ExportService _exports;
    private readonly InsightRunnerService _insights;
    private readonly TimeProvider _clock;
    private readonly IServiceScopeFactory _scopes;
    private readonly ExportWorkerIdentity _identity;
    private readonly ExportOwnershipService _ownership;

    public ExportJobProcessor(
        PulseDbContext db,
        ExportService exports,
        InsightRunnerService insights,
        TimeProvider clock,
        IServiceScopeFactory scopes,
        ExportWorkerIdentity identity)
    {
        _db = db;
        _exports = exports;
        _insights = insights;
        _clock = clock;
        _scopes = scopes;
        _identity = identity;
        _ownership = new(db, clock);
    }

    /// <summary>Claims at most 20 pending/abandoned jobs; all final writes require the current attempt.</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        var finished = await _ownership.RecoverCancelledAsync(ct);
        var now = _clock.GetUtcNow();
        var jobs = await _db.ExportJobs.AsNoTracking()
            .Where(j => j.Status == ExportJobStatus.Pending || (j.Status == ExportJobStatus.Running && (j.LeaseExpiresAt == null || j.LeaseExpiresAt <= now)))
            .OrderBy(j => j.CreatedAt)
            .ThenBy(j => j.Id).Select(j => new { j.ProjectId, j.Id }).Take(20)
            .ToListAsync(ct);

        foreach (var candidate in jobs)
        {
            var attempt = await _ownership.ClaimAsync(candidate.ProjectId, candidate.Id, _identity.Owner, ct);
            if (attempt is null) continue;
            var job = await _db.ExportJobs.AsNoTracking().SingleAsync(j => j.ProjectId == attempt.ProjectId && j.Id == attempt.JobId, ct);
            using var active = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var monitor = MonitorAsync(attempt, active);
            try
            {
                var (content, contentType, rows) = await ExecuteAsync(job, attempt, active.Token);
                await CheckAttemptAsync(attempt, active.Token);
                if (await _ownership.PublishAsync(attempt, content, contentType, rows, null, active.Token)) finished++;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (LostExportAttemptException) { }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                var code = ex switch { ExportSnapshotLimitException limit => limit.Code, MissingExportInsightException => "No such insight in this project.", _ => "export_failed" };
                if (await _ownership.PublishAsync(attempt, null, null, 0, code, ct)) finished++;
            }
            finally
            {
                active.Cancel();
                await monitor;
                _db.ChangeTracker.Clear();
            }
        }
        return finished;
    }

    private async Task MonitorAsync(ExportAttempt attempt, CancellationTokenSource active)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), active.Token);
                // A separate context can observe cancellation while the render context is querying.
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
                if (!await new ExportOwnershipService(db, _clock).HeartbeatAsync(attempt, active.Token)) { active.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (active.IsCancellationRequested) { }
        catch { active.Cancel(); throw; }
    }

    private async Task CheckAttemptAsync(ExportAttempt attempt, CancellationToken ct)
    {
        if (!await _ownership.HeartbeatAsync(attempt, ct)) throw new LostExportAttemptException();
    }

    private async Task<(string Content, string ContentType, int Rows)> ExecuteAsync(
        ExportJob job,
        ExportAttempt attempt,
        CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(job.ParamsJson) ? "{}" : job.ParamsJson);
        var config = doc.RootElement;

        return job.Type switch
        {
            "events" => await ExportEventsAsync(job, config, attempt, ct),
            "persons" => await ExportPersonsAsync(job, attempt, ct),
            "insight" => await ExportInsightAsync(job, config, ct),
            _ => throw new InvalidOperationException($"Unknown export type '{job.Type}'."),
        };
    }

    private async Task<(string, string, int)> ExportEventsAsync(
        ExportJob job,
        JsonElement config,
        ExportAttempt attempt,
        CancellationToken ct)
    {
        var eventName = GetString(config, "event");
        var from = GetString(config, "from") is { } f && DateTimeOffset.TryParse(f, out var parsedFrom)
            ? parsedFrom
            : (DateTimeOffset?)null;
        var to = GetString(config, "to") is { } t && DateTimeOffset.TryParse(t, out var parsedTo)
            ? parsedTo
            : (DateTimeOffset?)null;

        var filtersJson = config.TryGetProperty("filters", out var raw)
                          && raw.ValueKind == JsonValueKind.Array
            ? raw.GetRawText()
            : null;

        if (!PropertyFilterParser.TryParse(filtersJson, out var filters, out var filterError))
        {
            throw new InvalidOperationException(filterError);
        }

        var rows = new List<EventExportRow>();
        if (job.Consistency == "snapshot")
        {
            if (from is null || to is null || filters.Count > 0) throw new InvalidOperationException("invalid_snapshot_configuration");
            var snapshots = new ExportSnapshotService(_db, _ownership, _clock);
            await snapshots.CaptureAsync(attempt, eventName, from.Value, to.Value, ct);
            for (var offset = 0; ; offset += PageSize)
            {
                await CheckAttemptAsync(attempt, ct);
                var page = await snapshots.ReadPageAsync(attempt, offset, ct);
                rows.AddRange(page);
                if (page.Count < PageSize) break;
            }
            return job.Format == "csv" ? (ExportService.EventsCsv(rows), "text/csv", rows.Count)
                : (JsonSerializer.Serialize(new { events = rows }, JsonSerializerOptions.Web), "application/json", rows.Count);
        }
        string? cursor = null;
        while (rows.Count < MaxRows)
        {
            await CheckAttemptAsync(attempt, ct);
            var page = await _exports.EventsPageAsync(
                job.ProjectId, eventName, from, to, filters, cursor, PageSize, ct);
            rows.AddRange(page.Events);
            cursor = page.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        return job.Format == "csv"
            ? (ExportService.EventsCsv(rows), "text/csv", rows.Count)
            : (JsonSerializer.Serialize(new { events = rows }, JsonSerializerOptions.Web), "application/json", rows.Count);
    }

    private async Task<(string, string, int)> ExportPersonsAsync(ExportJob job, ExportAttempt attempt, CancellationToken ct)
    {
        var rows = new List<PersonExportRow>();
        string? cursor = null;
        while (rows.Count < MaxRows)
        {
            await CheckAttemptAsync(attempt, ct);
            var page = await _exports.PersonsPageAsync(job.ProjectId, cursor, PageSize, ct);
            rows.AddRange(page.Persons);
            cursor = page.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        return job.Format == "csv"
            ? (ExportService.PersonsCsv(rows), "text/csv", rows.Count)
            : (JsonSerializer.Serialize(new { persons = rows }, JsonSerializerOptions.Web), "application/json", rows.Count);
    }

    private async Task<(string, string, int)> ExportInsightAsync(
        ExportJob job,
        JsonElement config,
        CancellationToken ct)
    {
        var insightId = GetString(config, "insightId") is { } raw && Guid.TryParse(raw, out var parsed)
            ? parsed
            : throw new InvalidOperationException("Insight exports need an 'insightId'.");

        var insight = await _db.Insights
            .SingleOrDefaultAsync(i => i.ProjectId == job.ProjectId && i.Id == insightId, ct)
            ?? throw new MissingExportInsightException();

        var run = await _insights.RunAsync(insight, ct);
        if (!run.Ok)
        {
            throw new InvalidOperationException(run.Error);
        }

        if (job.Format == "csv")
        {
            var (content, rows) = ExportService.QueryResultCsv(run.Result!);
            return (content, "text/csv", rows);
        }

        return (JsonSerializer.Serialize(run.Result, JsonSerializerOptions.Web), "application/json", 1);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
