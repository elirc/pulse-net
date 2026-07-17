using Microsoft.EntityFrameworkCore;
using Pulse.Domain;

namespace Pulse.Infrastructure.Services;

public record TrendBucket(DateTimeOffset Start, int Count, int UniquePersons);

public record TrendAnnotation(Guid Id, DateOnly Date, string Content);

public record TrendResult(
    string Event,
    string Interval,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<TrendBucket> Buckets,
    IReadOnlyList<TrendAnnotation> Annotations);

public record TrendSeries(string Value, int Total, IReadOnlyList<TrendBucket> Buckets);

public record TrendBreakdownResult(
    string Event,
    string Interval,
    string Breakdown,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<TrendSeries> Series,
    IReadOnlyList<TrendAnnotation> Annotations);

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
/// to the ticks-based timestamp index); property filtering, bucketing, funnel
/// ordering, breakdowns and cohort math happen in memory on the filtered slice.
/// </summary>
public class QueryService
{
    /// <summary>Breakdown label for events missing the property.</summary>
    public const string NoneBreakdownValue = "(none)";

    /// <summary>Breakdown label aggregating values beyond the top N.</summary>
    public const string OtherBreakdownValue = "(other)";

    private readonly PulseDbContext _db;
    private readonly CohortService _cohorts;

    public QueryService(PulseDbContext db, CohortService cohorts)
    {
        _db = db;
        _cohorts = cohorts;
    }

    private sealed record QueryEvent(
        Guid? PersonId,
        string Name,
        DateTimeOffset Timestamp,
        string PropertiesJson);

    public async Task<TrendResult> TrendAsync(
        Guid projectId,
        string eventName,
        DateTimeOffset from,
        DateTimeOffset to,
        TrendInterval interval,
        IReadOnlyList<PropertyFilter>? filters = null,
        CancellationToken ct = default)
    {
        var events = await LoadEventsAsync(
            projectId, e => e.Name == eventName, from, to, filters, ct);

        var buckets = Bucketize(events, from, to, interval);
        var annotations = await LoadAnnotationsAsync(projectId, from, to, ct);

        return new TrendResult(
            eventName, interval.ToString().ToLowerInvariant(), from, to, buckets, annotations);
    }

    public async Task<TrendBreakdownResult> TrendBreakdownAsync(
        Guid projectId,
        string eventName,
        DateTimeOffset from,
        DateTimeOffset to,
        TrendInterval interval,
        string breakdownProperty,
        int breakdownLimit,
        IReadOnlyList<PropertyFilter>? filters = null,
        CancellationToken ct = default)
    {
        var events = await LoadEventsAsync(
            projectId, e => e.Name == eventName, from, to, filters, ct);

        var byValue = events
            .GroupBy(e => PropertyFilterEvaluator.GetValue(e.PropertiesJson, breakdownProperty)
                          ?? NoneBreakdownValue)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var series = byValue
            .Take(breakdownLimit)
            .Select(g => new TrendSeries(g.Key, g.Count(), Bucketize([.. g], from, to, interval)))
            .ToList();

        var overflow = byValue.Skip(breakdownLimit).SelectMany(g => g).ToList();
        if (overflow.Count > 0)
        {
            series.Add(new TrendSeries(
                OtherBreakdownValue, overflow.Count, Bucketize(overflow, from, to, interval)));
        }

        return new TrendBreakdownResult(
            eventName,
            interval.ToString().ToLowerInvariant(),
            breakdownProperty,
            from,
            to,
            series,
            await LoadAnnotationsAsync(projectId, from, to, ct));
    }

    public async Task<FunnelResult> FunnelAsync(
        Guid projectId,
        IReadOnlyList<string> steps,
        DateTimeOffset from,
        DateTimeOffset to,
        int windowDays,
        IReadOnlyList<PropertyFilter>? filters = null,
        CancellationToken ct = default)
    {
        var stepSet = steps.ToHashSet();

        var events = await LoadEventsAsync(
            projectId, e => stepSet.Contains(e.Name), from, to, filters, ct,
            requirePerson: true);

        var window = TimeSpan.FromDays(windowDays);
        var reachedCounts = new int[steps.Count];

        foreach (var personEvents in events.GroupBy(e => e.PersonId))
        {
            var reached = DeepestStepReached(
                personEvents
                    .OrderBy(e => e.Timestamp)
                    .Select(e => (e.Name, e.Timestamp)),
                steps,
                window);

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
        IReadOnlyList<PropertyFilter>? filters = null,
        CancellationToken ct = default)
    {
        var rangeStart = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var rangeEnd = rangeStart.AddDays(days);

        var events = await LoadEventsAsync(
            projectId, e => true, rangeStart, rangeEnd, filters, ct,
            requirePerson: true, endExclusive: true);

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
    /// Loads the time-sliced events for a query (name/time filtering in SQL)
    /// and applies property filters in memory — person-targeted filters are
    /// evaluated against the properties of the person who performed the event.
    /// </summary>
    private async Task<List<QueryEvent>> LoadEventsAsync(
        Guid projectId,
        System.Linq.Expressions.Expression<Func<Domain.Entities.AnalyticsEvent, bool>> namePredicate,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<PropertyFilter>? filters,
        CancellationToken ct,
        bool requirePerson = false,
        bool endExclusive = false)
    {
        var query = _db.Events
            .Where(e => e.ProjectId == projectId)
            .Where(namePredicate)
            .Where(e => e.Timestamp >= from)
            .Where(endExclusive
                ? e => e.Timestamp < to
                : e => e.Timestamp <= to);

        if (requirePerson)
        {
            query = query.Where(e => e.PersonId != null);
        }

        var events = await query
            .Select(e => new QueryEvent(e.PersonId, e.Name, e.Timestamp, e.PropertiesJson))
            .ToListAsync(ct);

        if (filters is not { Count: > 0 })
        {
            return events;
        }

        var eventFilters = filters.Where(f => f.Target == FilterTarget.Event).ToList();
        var personFilters = filters.Where(f => f.Target == FilterTarget.Person).ToList();
        var cohortFilters = filters.Where(f => f.Target == FilterTarget.Cohort).ToList();

        Dictionary<Guid, string> personProps = [];
        if (personFilters.Count > 0)
        {
            personProps = await _db.Persons
                .Where(p => p.ProjectId == projectId)
                .ToDictionaryAsync(p => p.Id, p => p.PropertiesJson, ct);
        }

        // Every cohort filter must match: intersect the member sets.
        HashSet<Guid>? cohortMembers = null;
        foreach (var cohortFilter in cohortFilters)
        {
            var cohortId = Guid.TryParse(cohortFilter.Value, out var parsed) ? parsed : Guid.Empty;
            var members = await _cohorts.GetMemberIdsAsync(projectId, cohortId, ct);

            if (cohortMembers is null)
            {
                cohortMembers = members;
            }
            else
            {
                cohortMembers.IntersectWith(members);
            }
        }

        return events.Where(e =>
        {
            if (!PropertyFilterEvaluator.Matches(e.PropertiesJson, eventFilters))
            {
                return false;
            }

            if (cohortMembers is not null
                && (e.PersonId is not { } cohortPersonId || !cohortMembers.Contains(cohortPersonId)))
            {
                return false;
            }

            if (personFilters.Count == 0)
            {
                return true;
            }

            var props = e.PersonId is { } personId
                ? personProps.GetValueOrDefault(personId)
                : null;

            return PropertyFilterEvaluator.Matches(props, personFilters);
        }).ToList();
    }

    /// <summary>Annotations whose date falls inside the queried range, oldest first.</summary>
    private async Task<List<TrendAnnotation>> LoadAnnotationsAsync(
        Guid projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var first = DateOnly.FromDateTime(from.UtcDateTime);
        var last = DateOnly.FromDateTime(to.UtcDateTime);

        return await _db.Annotations
            .Where(a => a.ProjectId == projectId && a.Date >= first && a.Date <= last)
            .OrderBy(a => a.Date)
            .Select(a => new TrendAnnotation(a.Id, a.Date, a.Content))
            .ToListAsync(ct);
    }

    private static List<TrendBucket> Bucketize(
        List<QueryEvent> events,
        DateTimeOffset from,
        DateTimeOffset to,
        TrendInterval interval)
    {
        var grouped = events
            .GroupBy(e => TimeBucket.Truncate(e.Timestamp, interval))
            .ToDictionary(
                g => g.Key,
                g => (Count: g.Count(), Unique: g.Select(e => e.PersonId).Distinct().Count()));

        return TimeBucket.Range(from, to, interval)
            .Select(start => grouped.TryGetValue(start, out var agg)
                ? new TrendBucket(start, agg.Count, agg.Unique)
                : new TrendBucket(start, 0, 0))
            .ToList();
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
