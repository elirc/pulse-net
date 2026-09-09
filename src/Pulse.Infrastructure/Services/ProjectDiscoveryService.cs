using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record EventNameUsage(string Event, long Count, DateTimeOffset FirstAt, DateTimeOffset LastAt);
public record EventUsage(DateTimeOffset From, DateTimeOffset To, long TotalEvents, int EventNameCount, IReadOnlyList<EventNameUsage> Items);
public record PropertyStringValue(string Value, long Count);
public record PropertyValues(string Event, string Property, long TotalEvents, IReadOnlyList<PropertyStringValue> Values,
    long MissingCount, long NullCount, long NonStringCount, long OtherStringCount);
public record PropertyValuesOutcome(int Status, PropertyValues? Result = null, string? Detail = null);
public record SafeProjectMetadata(Guid Id, string Name, DateTimeOffset CreatedAt);
public record ProjectOverviewCounts(long Persons, long Events, long Insights, long Dashboards, long Cohorts,
    long FeatureFlags, long ActiveFeatureFlags, long CompletedExports);
public record ProjectOverview(SafeProjectMetadata Project, DateTimeOffset ObservedAt, ProjectOverviewCounts Counts, ProjectIngestionMetrics Ingestion);

public sealed class ProjectDiscoveryService(PulseDbContext db, IngestionOperationsService ingestion, TimeProvider clock)
{
    public async Task<EventUsage> EventUsageAsync(Guid projectId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct)
    {
        var groups = await db.Events.AsNoTracking().Where(e => e.ProjectId == projectId && e.Timestamp >= from && e.Timestamp < to)
            .GroupBy(e => e.Name).Select(g => new EventNameUsage(g.Key, g.LongCount(), g.Min(e => e.Timestamp), g.Max(e => e.Timestamp))).ToListAsync(ct);
        // Aggregate in SQL; sort the compact name groups using precise .NET ordinal
        // ordering (SQLite UTF-8 BINARY ordering differs for some Unicode names).
        var items = groups.OrderByDescending(g => g.Count).ThenBy(g => g.Event, StringComparer.Ordinal).Take(limit).ToList();
        return new(from, to, groups.Sum(g => g.Count), groups.Count, items);
    }

    public async Task<PropertyValuesOutcome> PropertyValuesAsync(Guid projectId, string eventName, string property,
        DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct)
    {
        var rows = db.Events.AsNoTracking().Where(e => e.ProjectId == projectId && e.Name == eventName && e.Timestamp >= from && e.Timestamp < to)
            .OrderBy(e => e.Timestamp).ThenBy(e => e.Id).Select(e => e.PropertiesJson).Take(10001).AsAsyncEnumerable();
        long count = 0, bytes = 0, missing = 0, nulls = 0, nonString = 0;
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var json in rows.WithCancellation(ct))
        {
            count++;
            bytes += Encoding.UTF8.GetByteCount(json);
            if (count > 10000 || bytes > 8 * 1024 * 1024)
                return new(422, Detail: "The scan exceeds 10,000 events or 8 MiB of properties. Narrow the date range.");
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return new(409, Detail: "A stored properties document is not a JSON object.");
                if (!document.RootElement.TryGetProperty(property, out var value)) missing++;
                else if (value.ValueKind == JsonValueKind.Null) nulls++;
                else if (value.ValueKind != JsonValueKind.String) nonString++;
                else
                {
                    var text = value.GetString()!;
                    values[text] = values.GetValueOrDefault(text) + 1;
                }
            }
            catch (JsonException) { return new(409, Detail: "A stored properties document contains invalid JSON."); }
        }
        var top = values.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(limit)
            .Select(p => new PropertyStringValue(p.Key, p.Value)).ToList();
        return new(200, new(eventName, property, count, top, missing, nulls, nonString, values.Values.Sum() - top.Sum(v => v.Count)));
    }

    public async Task<ProjectOverview?> OverviewAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId)
            .Select(p => new SafeProjectMetadata(p.Id, p.Name, p.CreatedAt)).SingleOrDefaultAsync(ct);
        if (project is null) return null;
        var now = clock.GetUtcNow();
        var counts = new ProjectOverviewCounts(
            await db.Persons.LongCountAsync(p => p.ProjectId == projectId, ct),
            await db.Events.LongCountAsync(e => e.ProjectId == projectId, ct),
            await db.Insights.LongCountAsync(i => i.ProjectId == projectId, ct),
            await db.Dashboards.LongCountAsync(d => d.ProjectId == projectId, ct),
            await db.Cohorts.LongCountAsync(c => c.ProjectId == projectId, ct),
            await db.FeatureFlags.LongCountAsync(f => f.ProjectId == projectId, ct),
            await db.FeatureFlags.LongCountAsync(f => f.ProjectId == projectId && f.Active, ct),
            await db.ExportJobs.LongCountAsync(j => j.ProjectId == projectId && j.Status == ExportJobStatus.Completed, ct));
        return new(project, now, counts, await ingestion.GetMetricsAsync(projectId, now, ct));
    }
}
