using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.Services;

/// <summary>Only W3C identifiers cross the queue boundary; payloads and baggage never do.</summary>
public static class IngestionTrace
{
    public const string SourceName = "Pulse.Ingestion";
    public static readonly ActivitySource Source = new(SourceName);

    public static string? Normalize(string? parent) => TryParse(parent, out var context) ? Format(context) : null;
    public static bool TryParse(string? parent, out ActivityContext context)
    {
        context = default;
        return parent?.Length == 55 && ActivityContext.TryParse(parent, null, true, out context) &&
            context.TraceId != default && context.SpanId != default;
    }
    public static string Format(ActivityContext context) => $"00-{context.TraceId}-{context.SpanId}-{((byte)context.TraceFlags & 1):x2}";
    public static Activity Producer(string name, string? original = null) => Start(name, ActivityKind.Producer,
        Activity.Current?.Context ?? default, original);
    public static Activity Consumer(string? parent, string? original) => Start("ingestion.process", ActivityKind.Consumer,
        TryParse(parent, out var parsed) ? parsed : default, original);

    private static Activity Start(string name, ActivityKind kind, ActivityContext parent, string? original)
    {
        var links = TryParse(original, out var linked) ? new[] { new ActivityLink(linked) } : [];
        var activity = Source.StartActivity(name, kind, parent, links: links);
        if (activity is not null) return activity;
        // ActivitySource intentionally returns null when no listener samples the span.
        // Keep local correlation functional without requiring a collector or listener.
        var local = new Activity(name).SetIdFormat(ActivityIdFormat.W3C);
        if (parent.TraceId != default) local.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
        return local.Start();
    }

    public static void Log(ILogger? logger, Activity activity, string outcome, long? sequence = null, int? attempt = null, int? duplicates = null)
    {
        activity.SetTag("pulse.outcome", outcome);
        if (sequence.HasValue) activity.SetTag("pulse.queue.sequence", sequence.Value);
        if (attempt.HasValue) activity.SetTag("pulse.attempt", attempt.Value);
        if (duplicates.HasValue) activity.SetTag("pulse.duplicates", duplicates.Value);
        logger?.LogInformation("Ingestion {Outcome} trace {TraceId} span {SpanId} queue {QueueSequence} attempt {Attempt} duplicates {Duplicates}",
            outcome, activity.TraceId.ToString(), activity.SpanId.ToString(), sequence, attempt, duplicates);
    }
}
