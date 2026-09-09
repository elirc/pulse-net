using System.Text.Json;

namespace Pulse.Infrastructure.Services;

public record ReplayIssue(string Code, string Field, string Message);
public record ReplayValidation(bool Replayable, IReadOnlyList<ReplayIssue> Issues);

public static class ReplayEnvelopeValidator
{
    public static ReplayValidation Validate(string payload)
    {
        IncomingEvent? incoming;
        try { incoming = JsonSerializer.Deserialize<IncomingEvent>(payload); }
        catch (JsonException) { return new(false, [new("invalid-envelope-json", "payload", "Stored envelope is not valid event JSON.")]); }
        if (incoming is null) return new(false, [new("missing-envelope", "payload", "Stored event envelope is missing.")]);
        var issues = new List<ReplayIssue>();
        if (string.IsNullOrWhiteSpace(incoming.Name)) issues.Add(new("missing-event-name", "name", "Event name is required."));
        if (string.IsNullOrWhiteSpace(incoming.DistinctId)) issues.Add(new("missing-distinct-id", "distinctId", "Distinct ID is required."));
        if (string.IsNullOrWhiteSpace(incoming.PropertiesJson)) issues.Add(new("missing-properties-json", "propertiesJson", "Properties JSON is required."));
        else
        {
            try
            {
                using var properties = JsonDocument.Parse(incoming.PropertiesJson);
                if (properties.RootElement.ValueKind != JsonValueKind.Object)
                    issues.Add(new("properties-not-object", "propertiesJson", "Properties must be a JSON object."));
            }
            catch (JsonException) { issues.Add(new("invalid-properties-json", "propertiesJson", "Properties are not valid JSON.")); }
        }
        return new(issues.Count == 0, issues);
    }
}
