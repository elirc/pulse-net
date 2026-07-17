using Microsoft.EntityFrameworkCore;
using Pulse.Domain;

namespace Pulse.Infrastructure.Services;

public record TrendBucket(DateTimeOffset Start, int Count, int UniquePersons);

public record TrendResult(
    string Event,
    string Interval,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<TrendBucket> Buckets);

public record FunnelStepResult(
    int Order,
    string Event,
    int Persons,
    double ConversionFromPrevious,
    double ConversionFromFirst);

public record FunnelResult(
    DateTimeOffset From,
    DateTimeOffset To,
    int WindowDays,
    IReadOnlyList<FunnelStepResult> Steps);

public record RetentionCohort(DateOnly CohortDate, int Size, IReadOnlyList<int> ReturnedByDay);

public record RetentionResult(
    DateOnly From,
    int Days,
    string? TargetEvent,
    IReadOnlyList<RetentionCohort> Cohorts);

/// <summary>
/// The analytics read side. Time-range filtering happens in SQL (fast thanks
/// to the ticks-based timestamp index); bucketing, funnel ordering and cohort
/// math happen in memory on the filtered slice.
/// </summary>
public class QueryService
{
    private readonly PulseDbContext _db;

    public QueryService(PulseDbContext db)
    {
        _db = db;
    }

    public async Task<TrendResult> TrendAsync(
        Guid projectId,
        string eventName,
        DateTimeOffset from,
        DateTimeOffset to,
        TrendInterval interval,
        CancellationToken ct = default)
    {
        var events = await _db.Events
            .Where(e => e.ProjectId == projectId
                        && e.Name == eventName
                        && e.Timestamp >= from
                        && e.Timestamp <= to)
            .Select(e => new { e.Timestamp, e.PersonId })
            .ToListAsync(ct);

        var grouped = events
            .GroupBy(e => TimeBucket.Truncate(e.Timestamp, interval))
            .ToDictionary(
                g => g.Key,
                g => (Count: g.Count(), Unique: g.Select(e => e.PersonId).Distinct().Count()));

        var buckets = TimeBucket.Range(from, to, interval)
            .Select(start => grouped.TryGetValue(start, out var agg)
                ? new TrendBucket(start, agg.Count, agg.Unique)
                : new TrendBucket(start, 0, 0))
            .ToList();

        return new TrendResult(eventName, interval.ToString().ToLowerInvariant(), from, to, buckets);
    }

    public async Task<FunnelResult> FunnelAsync(
        Guid projectId,
        IReadOnlyList<string> steps,
        DateTimeOffset from,
        DateTimeOffset to,
        int windowDays,
        CancellationToken ct = default)
    {
        var stepSet = steps.ToHashSet();

        var events = await _db.Events
            .Where(e => e.ProjectId == projectId
                        && stepSet.Contains(e.Name)
                        && e.Timestamp >= from
                        && e.Timestamp <= to
                        && e.PersonId != null)
            .OrderBy(e => e.Timestamp)
            .Select(e => new { e.PersonId, e.Name, e.Timestamp })
            .ToListAsync(ct);

        var window = TimeSpan.FromDays(windowDays);
        var reachedCounts = new int[steps.Count];

        foreach (var personEvents in events.GroupBy(e => e.PersonId))
        {
            var reached = DeepestStepReached(
                personEvents.Select(e => (e.Name, e.Timestamp)), steps, window);

            for (var i = 0; i < reached; i++)
            {
                reachedCounts[i]++;
            }
        }

        var results = new List<FunnelStepResult>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var previous = i == 0 ? reachedCounts[0] : reachedCounts[i - 1];
            var first = reachedCounts[0];

            results.Add(new FunnelStepResult(
                Order: i + 1,
                Event: steps[i],
                Persons: reachedCounts[i],
                ConversionFromPrevious: i == 0 ? 1.0 : Ratio(reachedCounts[i], previous),
                ConversionFromFirst: Ratio(reachedCounts[i], first)));
        }

        return new FunnelResult(from, to, windowDays, results);
    }

    public async Task<RetentionResult> RetentionAsync(
        Guid projectId,
        DateOnly from,
        int days,
        string? targetEvent,
        CancellationToken ct = default)
    {
        var rangeStart = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(days);

        var events = await _db.Events
            .Where(e => e.ProjectId == projectId
                        && e.Timestamp >= rangeStart
                        && e.Timestamp < rangeEnd
                        && e.PersonId != null)
            .Select(e => new { e.PersonId, e.Name, e.Timestamp })
            .ToListAsync(ct);

        // Cohort entry: the person's first qualifying event inside the window.
        var cohortOf = events
            .Where(e => targetEvent is null || e.Name == targetEvent)
            .GroupBy(e => e.PersonId)
            .ToDictionary(
                g => g.Key,
                g => DateOnly.FromDateTime(g.Min(e => e.Timestamp).UtcDateTime));

        // Activity: every day each person did anything at all.
        var activeDays = events
            .GroupBy(e => e.PersonId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => DateOnly.FromDateTime(e.Timestamp.UtcDateTime)).ToHashSet());

        var cohorts = new List<RetentionCohort>(days);
        for (var d = 0; d < days; d++)
        {
            var cohortDate = from.AddDays(d);
            var members = cohortOf.Where(kv => kv.Value == cohortDate).Select(kv => kv.Key).ToList();

            var horizon = days - d; // Triangle: later cohorts have fewer observable days.
            var returned = new int[horizon];
            for (var n = 0; n < horizon; n++)
            {
                var day = cohortDate.AddDays(n);
                returned[n] = members.Count(m => activeDays[m].Contains(day));
            }

            cohorts.Add(new RetentionCohort(cohortDate, members.Count, returned));
        }

        return new RetentionResult(from, days, targetEvent, cohorts);
    }

    /// <summary>
    /// Walks a person's chronologically-ordered events and returns how many
    /// funnel steps they completed in order, starting the clock at their
    /// earliest step-1 event.
    /// </summary>
    private static int DeepestStepReached(
        IEnumerable<(string Name, DateTimeOffset Timestamp)> orderedEvents,
        IReadOnlyList<string> steps,
        TimeSpan window)
    {
        var next = 0;
        DateTimeOffset? funnelStart = null;

        foreach (var (name, timestamp) in orderedEvents)
        {
            if (next >= steps.Count)
            {
                break;
            }

            if (name != steps[next])
            {
                continue;
            }

            if (funnelStart is not null && timestamp - funnelStart.Value > window)
            {
                break; // Conversion window expired.
            }

            funnelStart ??= timestamp;
            next++;
        }

        return next;
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0.0 : Math.Round((double)numerator / denominator, 4);
}
