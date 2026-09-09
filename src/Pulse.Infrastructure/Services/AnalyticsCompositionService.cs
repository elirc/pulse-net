using Microsoft.EntityFrameworkCore;
using Pulse.Domain;

namespace Pulse.Infrastructure.Services;

public record PeriodComparison(DateTimeOffset From, DateTimeOffset To, DateTimeOffset PreviousFrom,
    DateTimeOffset PreviousTo, long CurrentCount, long PreviousCount, long Delta, double? PercentChange);
public record EventTrendSeries(string Event, IReadOnlyList<TrendBucket> Buckets);
public record MultiTrendResult(DateTimeOffset From, DateTimeOffset To, string Interval,
    IReadOnlyList<EventTrendSeries> Series, IReadOnlyList<TrendAnnotation> Annotations);

public sealed class AnalyticsCompositionService(PulseDbContext db, QueryService queries)
{
    public async Task<PeriodComparison> CompareAsync(Guid projectId, string name,
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset previousFrom, CancellationToken ct)
    {
        var events = db.Events.Where(e => e.ProjectId == projectId && e.Name == name);
        var current = await events.LongCountAsync(e => e.Timestamp >= from && e.Timestamp < to, ct);
        var previous = await events.LongCountAsync(e => e.Timestamp >= previousFrom && e.Timestamp < from, ct);
        var delta = current - previous;
        var percent = previous == 0 ? (double?)null : Math.Round(delta * 100d / previous, 2, MidpointRounding.AwayFromZero);
        return new(from, to, previousFrom, from, current, previous, delta, percent);
    }

    public async Task<MultiTrendResult> MultiTrendAsync(Guid projectId, IReadOnlyList<string> names,
        DateTimeOffset from, DateTimeOffset to, TrendInterval interval, CancellationToken ct)
    {
        var series = new List<EventTrendSeries>();
        IReadOnlyList<TrendAnnotation> annotations = [];
        foreach (var name in names)
        {
            var result = await queries.TrendAsync(projectId, name, from, to, interval, [], ct);
            if (series.Count == 0) annotations = result.Annotations;
            series.Add(new(name, result.Buckets));
        }
        return new(from, to, interval.ToString().ToLowerInvariant(), series, annotations);
    }
}
