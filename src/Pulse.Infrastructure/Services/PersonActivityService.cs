using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Services;

public record PersonTimelineEvent(Guid Id, string Event, DateTimeOffset Timestamp, string DistinctId, JsonElement Properties);
public record PersonTimelinePage(IReadOnlyList<PersonTimelineEvent> Events, string? NextCursor);
public record NamedEventCount(string Event, long Count);
public record PersonActivitySummary(Guid PersonId, DateTimeOffset From, DateTimeOffset To, long TotalEvents,
    int ActiveUtcDays, DateTimeOffset? FirstEventAt, DateTimeOffset? LastEventAt, IReadOnlyList<NamedEventCount> TopEvents);

public sealed class PersonActivityService(PulseDbContext db)
{
    public async Task<PersonTimelinePage> TimelineAsync(Guid projectId, Guid personId, string? name, int limit, ScopedCursor? cursor, CancellationToken ct)
    {
        var query = db.Events.AsNoTracking().Where(e => e.ProjectId == projectId && e.PersonId == personId);
        if (name is not null) query = query.Where(e => e.Name == name);
        if (cursor is not null)
        {
            var timestamp = new DateTimeOffset(cursor.Ticks, TimeSpan.Zero);
            var id = cursor.Id;
            query = query.Where(e => e.Timestamp < timestamp || (e.Timestamp == timestamp && e.Id.CompareTo(id) < 0));
        }
        var rows = await query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id).Take(limit + 1).ToListAsync(ct);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        var next = more ? new ScopedCursor(1, "person-timeline", projectId, personId, name, rows[^1].Timestamp.UtcTicks, rows[^1].Id).Encode() : null;
        return new(rows.Select(e => new PersonTimelineEvent(e.Id, e.Name, e.Timestamp, e.DistinctId,
            JsonSerializer.Deserialize<JsonElement>(e.PropertiesJson))).ToList(), next);
    }

    public async Task<PersonActivitySummary> SummaryAsync(Guid projectId, Guid personId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var query = db.Events.AsNoTracking().Where(e => e.ProjectId == projectId && e.PersonId == personId && e.Timestamp >= from && e.Timestamp < to);
        var total = await query.LongCountAsync(ct);
        var first = await query.MinAsync(e => (DateTimeOffset?)e.Timestamp, ct);
        var last = await query.MaxAsync(e => (DateTimeOffset?)e.Timestamp, ct);
        // The UTC ticks converter does not translate UtcDateTime.Date. Load timestamps only;
        // this fallback is proportional to matching events, not a constant-memory aggregation.
        var timestamps = await query.Select(e => e.Timestamp).ToListAsync(ct);
        var days = timestamps.Select(t => t.UtcDateTime.Date).Distinct().Count();
        var counts = await query.GroupBy(e => e.Name).Select(g => new NamedEventCount(g.Key, g.LongCount())).ToListAsync(ct);
        var top = counts.OrderByDescending(e => e.Count).ThenBy(e => e.Event, StringComparer.Ordinal).Take(5).ToList();
        return new(personId, from, to, total, days, first, last, top);
    }
}
