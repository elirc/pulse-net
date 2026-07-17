using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record EventExportRow(
    Guid Id,
    DateTimeOffset Timestamp,
    string Event,
    string DistinctId,
    Guid? PersonId,
    JsonElement Properties);

public record EventExportPage(IReadOnlyList<EventExportRow> Events, string? NextCursor);

public record PersonExportRow(
    Guid Id,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> DistinctIds,
    JsonElement Properties);

public record PersonExportPage(IReadOnlyList<PersonExportRow> Persons, string? NextCursor);

/// <summary>
/// Data export: cursor-paginated event/person reads, plus CSV/JSON rendering
/// shared by the synchronous endpoints and the async job processor. Cursors
/// encode the last scanned row's sort key, so pages are stable under new
/// writes (events only ever append after the cursor).
/// </summary>
public class ExportService
{
    public const int MaxPageSize = 1000;

    private readonly PulseDbContext _db;

    public ExportService(PulseDbContext db)
    {
        _db = db;
    }

    // --- Cursors ------------------------------------------------------------

    public static string EncodeCursor(DateTimeOffset timestamp, Guid id) =>
        Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{timestamp.UtcTicks}:{id:N}"));

    public static bool TryDecodeCursor(string? cursor, out DateTimeOffset timestamp, out Guid id)
    {
        timestamp = default;
        id = default;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true; // No cursor: start from the beginning.
        }

        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split(':');
            if (parts.Length != 2
                || !long.TryParse(parts[0], out var ticks)
                || !Guid.TryParse(parts[1], out id))
            {
                return false;
            }

            timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // --- Events ---------------------------------------------------------------

    /// <summary>
    /// One page of events ordered by (timestamp, id). Property filters are
    /// applied after the SQL scan, so a filtered page may hold fewer than
    /// <paramref name="limit"/> rows while the cursor still advances.
    /// </summary>
    public async Task<EventExportPage> EventsPageAsync(
        Guid projectId,
        string? eventName,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IReadOnlyList<PropertyFilter> filters,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        if (!TryDecodeCursor(cursor, out var afterTimestamp, out var afterId))
        {
            throw new ArgumentException("Invalid cursor.", nameof(cursor));
        }

        var query = _db.Events.Where(e => e.ProjectId == projectId);

        if (!string.IsNullOrWhiteSpace(eventName))
        {
            query = query.Where(e => e.Name == eventName);
        }

        if (from is { } start)
        {
            query = query.Where(e => e.Timestamp >= start);
        }

        if (to is { } end)
        {
            query = query.Where(e => e.Timestamp <= end);
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query = query.Where(e =>
                e.Timestamp > afterTimestamp
                || (e.Timestamp == afterTimestamp && e.Id.CompareTo(afterId) > 0));
        }

        var scanned = await query
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = scanned.Count > limit;
        if (hasMore)
        {
            scanned.RemoveAt(scanned.Count - 1);
        }

        var eventFilters = filters.Where(f => f.Target == FilterTarget.Event).ToList();
        var rows = scanned
            .Where(e => PropertyFilterEvaluator.Matches(e.PropertiesJson, eventFilters))
            .Select(e => new EventExportRow(
                e.Id,
                e.Timestamp,
                e.Name,
                e.DistinctId,
                e.PersonId,
                JsonSerializer.Deserialize<JsonElement>(e.PropertiesJson)))
            .ToList();

        var nextCursor = hasMore
            ? EncodeCursor(scanned[^1].Timestamp, scanned[^1].Id)
            : null;

        return new EventExportPage(rows, nextCursor);
    }

    // --- Persons ------------------------------------------------------------------

    public async Task<PersonExportPage> PersonsPageAsync(
        Guid projectId,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        if (!TryDecodeCursor(cursor, out var afterCreated, out var afterId))
        {
            throw new ArgumentException("Invalid cursor.", nameof(cursor));
        }

        var query = _db.Persons.Where(p => p.ProjectId == projectId);

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query = query.Where(p =>
                p.CreatedAt > afterCreated
                || (p.CreatedAt == afterCreated && p.Id.CompareTo(afterId) > 0));
        }

        var scanned = await query
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = scanned.Count > limit;
        if (hasMore)
        {
            scanned.RemoveAt(scanned.Count - 1);
        }

        var personIds = scanned.Select(p => p.Id).ToList();
        var distinctIds = await _db.PersonDistinctIds
            .Where(m => personIds.Contains(m.PersonId))
            .GroupBy(m => m.PersonId)
            .ToDictionaryAsync(
                g => g.Key,
                g => g.Select(m => m.DistinctId).Order().ToList(),
                ct);

        var rows = scanned
            .Select(p => new PersonExportRow(
                p.Id,
                p.CreatedAt,
                distinctIds.GetValueOrDefault(p.Id, []),
                JsonSerializer.Deserialize<JsonElement>(p.PropertiesJson)))
            .ToList();

        var nextCursor = hasMore
            ? EncodeCursor(scanned[^1].CreatedAt, scanned[^1].Id)
            : null;

        return new PersonExportPage(rows, nextCursor);
    }

    // --- CSV rendering ---------------------------------------------------------------

    public static string EventsCsv(IEnumerable<EventExportRow> rows) =>
        Csv.Document(new[] { Csv.Line("id", "timestamp", "event", "distinct_id", "person_id", "properties") }
            .Concat(rows.Select(r => Csv.Line(
                r.Id.ToString(),
                r.Timestamp.UtcDateTime.ToString("O"),
                r.Event,
                r.DistinctId,
                r.PersonId?.ToString(),
                r.Properties.GetRawText()))));

    public static string PersonsCsv(IEnumerable<PersonExportRow> rows) =>
        Csv.Document(new[] { Csv.Line("id", "created_at", "distinct_ids", "properties") }
            .Concat(rows.Select(r => Csv.Line(
                r.Id.ToString(),
                r.CreatedAt.UtcDateTime.ToString("O"),
                string.Join(';', r.DistinctIds),
                r.Properties.GetRawText()))));

    /// <summary>Renders a query-engine result as CSV rows; falls back to JSON-in-one-cell.</summary>
    public static (string Content, int Rows) QueryResultCsv(object result) => result switch
    {
        TrendResult trend => (
            Csv.Document(new[] { Csv.Line("bucket_start", "count", "unique_persons") }
                .Concat(trend.Buckets.Select(b => Csv.Line(
                    b.Start.UtcDateTime.ToString("O"),
                    b.Count.ToString(),
                    b.UniquePersons.ToString())))),
            trend.Buckets.Count),

        TrendBreakdownResult breakdown => (
            Csv.Document(new[] { Csv.Line("value", "bucket_start", "count", "unique_persons") }
                .Concat(breakdown.Series.SelectMany(s => s.Buckets.Select(b => Csv.Line(
                    s.Value,
                    b.Start.UtcDateTime.ToString("O"),
                    b.Count.ToString(),
                    b.UniquePersons.ToString()))))),
            breakdown.Series.Sum(s => s.Buckets.Count)),

        FunnelResult funnel => (
            Csv.Document(new[] { Csv.Line("order", "event", "persons", "conversion_from_previous", "conversion_from_first") }
                .Concat(funnel.Steps.Select(s => Csv.Line(
                    s.Order.ToString(),
                    s.Event,
                    s.Persons.ToString(),
                    s.ConversionFromPrevious.ToString("0.####"),
                    s.ConversionFromFirst.ToString("0.####"))))),
            funnel.Steps.Count),

        RetentionResult retention => (
            Csv.Document(new[] { Csv.Line("cohort_date", "size", "day", "returned") }
                .Concat(retention.Cohorts.SelectMany(c => c.ReturnedByDay.Select((returned, day) => Csv.Line(
                    c.CohortDate.ToString("O"),
                    c.Size.ToString(),
                    day.ToString(),
                    returned.ToString()))))),
            retention.Cohorts.Sum(c => c.ReturnedByDay.Count)),

        _ => (Csv.Document([Csv.Line("result"), Csv.Line(JsonSerializer.Serialize(result))]), 1),
    };
}
