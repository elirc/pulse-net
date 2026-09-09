namespace Pulse.Infrastructure.Services;

public record IngestionThresholds(int MaxPendingAgeSeconds = 60, int MaxPending = 1000);
public record IngestionStatus(string Status, DateTimeOffset ObservedAt, ProjectIngestionMetrics Metrics,
    IngestionThresholds Thresholds, IReadOnlyList<string> Reasons);

public static class IngestionStatusPolicy
{
    public static IngestionStatus Classify(ProjectIngestionMetrics metrics, IngestionThresholds thresholds, DateTimeOffset observedAt)
    {
        var reasons = new List<string>();
        if (metrics.DeadLetters > 0) reasons.Add("dead_letters_present");
        if (metrics.Pending >= thresholds.MaxPending) reasons.Add("queue_depth_high");
        if (metrics.OldestPendingAgeSeconds is { } age && age >= thresholds.MaxPendingAgeSeconds) reasons.Add("oldest_pending_too_old");
        return new(reasons.Count == 0 ? "ok" : "attention", observedAt, metrics, thresholds, reasons);
    }
}
