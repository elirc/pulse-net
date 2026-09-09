using System.Text.Json;
using Pulse.Api.Contracts;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

/// <summary>Pure envelope normalization shared by real admission and no-write preview.</summary>
public static class CaptureRequestParser
{
    public const int MaxBatchSize = 1000;
    public static (List<IncomingEvent> Events, Dictionary<string, string[]> Errors) Parse(CaptureRequest request)
    {
        var events = new List<IncomingEvent>();
        var errors = new Dictionary<string, string[]>();
        void Add(string? name, string? identity, DateTimeOffset? timestamp, JsonElement? properties, Guid? eventId, string prefix)
        {
            if (string.IsNullOrWhiteSpace(name)) errors[prefix + "event"] = ["Event name is required."];
            if (string.IsNullOrWhiteSpace(identity)) errors[prefix + "distinct_id"] = ["distinct_id is required."];
            if (eventId == Guid.Empty) errors[prefix + "event_id"] = ["event_id must be a nonempty UUID."];
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(identity))
            {
                var json = properties is { ValueKind: JsonValueKind.Object } value ? value.GetRawText() : "{}";
                if (eventId is not null && !CaptureFingerprint.TryCreate(name, identity, timestamp, json, out _))
                    errors[prefix + "properties"] = ["ID-bearing events must not contain duplicate object member names."];
                events.Add(new IncomingEvent(name.Trim(), identity.Trim(), timestamp, json, eventId));
            }
        }
        if (request.Batch is { } batch)
        {
            if (batch.Count == 0) errors["batch"] = ["Batch must contain at least one event."];
            else if (batch.Count > MaxBatchSize) errors["batch"] = [$"Batch exceeds the maximum of {MaxBatchSize} events."];
            else for (var index = 0; index < batch.Count; index++)
            {
                var item = batch[index];
                if (item is null) errors[$"batch[{index}]"] = ["Batch items must be event objects."];
                else Add(item.Event, item.DistinctId, item.Timestamp, item.Properties, item.EventId, $"batch[{index}].");
            }
        }
        else Add(request.Event, request.DistinctId, request.Timestamp, request.Properties, request.EventId, "");
        return (events, errors);
    }
}
