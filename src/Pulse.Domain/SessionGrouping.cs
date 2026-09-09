namespace Pulse.Domain;

public readonly record struct EventPosition(Guid Id, DateTimeOffset Timestamp);

public sealed record ActivitySession(
    int Ordinal,
    DateTimeOffset FirstTimestamp,
    DateTimeOffset LastTimestamp,
    Guid FirstEventId,
    Guid LastEventId,
    int EventCount,
    double ObservedDurationSeconds,
    bool MayStartBeforeWindow,
    bool MayContinueAfterWindow);

public static class SessionGrouping
{
    public static IReadOnlyList<ActivitySession> Group(
        IEnumerable<EventPosition> orderedPositions,
        TimeSpan gap,
        QueryWorkBudget budget,
        CancellationToken cancellationToken = default)
    {
        if (gap <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gap));
        var result = new List<ActivitySession>();
        EventPosition? first = null;
        EventPosition previous = default;
        var count = 0;

        foreach (var position in orderedPositions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (first is not null && position.Timestamp - previous.Timestamp >= gap)
            {
                Add(first.Value, previous, count);
                first = null;
                count = 0;
            }
            first ??= position;
            previous = position;
            count++;
        }

        if (first is not null) Add(first.Value, previous, count);
        return result;

        void Add(EventPosition start, EventPosition end, int eventCount)
        {
            budget.AddOutput();
            result.Add(new ActivitySession(
                result.Count + 1, start.Timestamp, end.Timestamp, start.Id, end.Id,
                eventCount, (end.Timestamp - start.Timestamp).TotalSeconds,
                MayStartBeforeWindow: false,
                MayContinueAfterWindow: false));
        }
    }

    public static IReadOnlyList<ActivitySession> MarkWindowBoundaries(IReadOnlyList<ActivitySession> sessions)
    {
        if (sessions.Count == 0) return sessions;
        var copy = sessions.ToArray();
        copy[0] = copy[0] with { MayStartBeforeWindow = true };
        copy[^1] = copy[^1] with { MayContinueAfterWindow = true };
        return copy;
    }
}
