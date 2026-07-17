using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

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

    public ExportJobProcessor(
        PulseDbContext db,
        ExportService exports,
        InsightRunnerService insights,
        TimeProvider clock)
    {
        _db = db;
        _exports = exports;
        _insights = insights;
        _clock = clock;
    }

    /// <summary>Runs every pending job once. Returns how many jobs finished (or failed).</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        var jobs = await _db.ExportJobs
            .Where(j => j.Status == ExportJobStatus.Pending)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        foreach (var job in jobs)
        {
            job.Status = ExportJobStatus.Running;
            await _db.SaveChangesAsync(ct);

            try
            {
                var (content, contentType, rows) = await ExecuteAsync(job, ct);
                job.ResultContent = content;
                job.ContentType = contentType;
                job.RowCount = rows;
                job.Status = ExportJobStatus.Completed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                job.Status = ExportJobStatus.Failed;
                job.Error = ex.Message;
            }

            job.CompletedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }

        return jobs.Count;
    }

    private async Task<(string Content, string ContentType, int Rows)> ExecuteAsync(
        ExportJob job,
        CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(job.ParamsJson) ? "{}" : job.ParamsJson);
        var config = doc.RootElement;

        return job.Type switch
        {
            "events" => await ExportEventsAsync(job, config, ct),
            "persons" => await ExportPersonsAsync(job, ct),
            "insight" => await ExportInsightAsync(job, config, ct),
            _ => throw new InvalidOperationException($"Unknown export type '{job.Type}'."),
        };
    }

    private async Task<(string, string, int)> ExportEventsAsync(
        ExportJob job,
        JsonElement config,
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
        string? cursor = null;
        while (rows.Count < MaxRows)
        {
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

    private async Task<(string, string, int)> ExportPersonsAsync(ExportJob job, CancellationToken ct)
    {
        var rows = new List<PersonExportRow>();
        string? cursor = null;
        while (rows.Count < MaxRows)
        {
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
            ?? throw new InvalidOperationException("No such insight in this project.");

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
