using System.Text;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain;

namespace Pulse.Infrastructure.Services;

public sealed class QueryDeadlineExceededException : Exception
{
    public QueryDeadlineExceededException() : base("The server execution deadline expired.") { }
}

public sealed class BoundedQueryService(PulseDbContext db)
{
    public static readonly QueryBudgetLimits TrendLimits = new(10_000, 8 * 1024 * 1024, 2_161, 1_000, TimeSpan.FromSeconds(2));
    private sealed record ScanRow(Guid Id, Guid? PersonId, DateTimeOffset Timestamp, string PropertiesJson);

    public async Task<TrendResult> TrendAsync(Guid projectId, string eventName, DateTimeOffset from,
        DateTimeOffset to, TrendInterval interval, IReadOnlyList<PropertyFilter> filters, CancellationToken clientCancellation)
    {
        if (string.IsNullOrWhiteSpace(eventName) || eventName.Length > 200) throw new ArgumentException("Event must contain 1 through 200 characters.", nameof(eventName));
        if (from >= to || to - from > TimeSpan.FromDays(90)) throw new ArgumentOutOfRangeException(nameof(from));
        if (!Enum.IsDefined(interval)) throw new ArgumentOutOfRangeException(nameof(interval));
        if (filters.Any(f => f.Target != FilterTarget.Event)) throw new ArgumentException("Only event filters are supported.", nameof(filters));
        var budget = new QueryWorkBudget(TrendLimits);
        List<DateTimeOffset> starts;
        try { starts = SafeBucketStarts(from, to, interval, TrendLimits.MaxBuckets + 1); }
        catch (ArgumentOutOfRangeException) { throw new QueryBudgetExceededException(QueryBudgetReason.BucketLimit, "The requested timestamps cannot be bucketed safely."); }
        budget.ReserveBuckets(starts.Count);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(clientCancellation);
        deadline.CancelAfter(TrendLimits.Deadline);
        try
        {
            var needsProperties = filters.Count > 0;
            var source = db.Events.AsNoTracking()
                .Where(e => e.ProjectId == projectId && e.Name == eventName && e.Timestamp >= from && e.Timestamp <= to)
                .OrderBy(e => e.Timestamp).ThenBy(e => e.Id)
                .Take(TrendLimits.MaxRows + 1);
            var query = needsProperties
                ? source.Select(e => new ScanRow(e.Id, e.PersonId, e.Timestamp, e.PropertiesJson))
                : source.Select(e => new ScanRow(e.Id, e.PersonId, e.Timestamp, ""));

            var counts = starts.ToDictionary(s => s, _ => 0);
            var persons = starts.ToDictionary(s => s, _ => new HashSet<Guid?>());
            await foreach (var row in query.AsAsyncEnumerable().WithCancellation(deadline.Token))
            {
                deadline.Token.ThrowIfCancellationRequested();
                budget.AddScannedRow(needsProperties ? Encoding.UTF8.GetByteCount(row.PropertiesJson) : 0);
                if (!PropertyFilterEvaluator.Matches(row.PropertiesJson, filters)) continue;
                var bucket = TimeBucket.Truncate(row.Timestamp, interval);
                counts[bucket]++;
                persons[bucket].Add(row.PersonId); // Compatibility: null is one distinct value.
            }

            var firstDate = DateOnly.FromDateTime(from.UtcDateTime);
            var lastDate = DateOnly.FromDateTime(to.UtcDateTime);
            var annotations = await db.Annotations.AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.Date >= firstDate && a.Date <= lastDate)
                .OrderBy(a => a.Date).ThenBy(a => a.Id)
                .Take(TrendLimits.MaxOutputs + 1)
                .Select(a => new TrendAnnotation(a.Id, a.Date, a.Content))
                .ToListAsync(deadline.Token);
            foreach (var _ in annotations) budget.AddAnnotation();

            deadline.Token.ThrowIfCancellationRequested();
            return new TrendResult(eventName, interval.ToString().ToLowerInvariant(), from, to,
                starts.Select(s => new TrendBucket(s, counts[s], persons[s].Count)).ToList(), annotations);
        }
        catch (OperationCanceledException) when (!clientCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new QueryDeadlineExceededException();
        }
    }

    private static List<DateTimeOffset> SafeBucketStarts(DateTimeOffset from, DateTimeOffset to,
        TrendInterval interval, int stopAfter)
    {
        var result = new List<DateTimeOffset>(Math.Min(stopAfter, 256));
        var current = TimeBucket.Truncate(from, interval);
        var final = TimeBucket.Truncate(to, interval);
        while (true)
        {
            result.Add(current);
            if (result.Count >= stopAfter || current >= final) return result;
            current = TimeBucket.Next(current, interval);
        }
    }
}
