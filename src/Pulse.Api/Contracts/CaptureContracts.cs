using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse.Api.Contracts;

/// <summary>
/// Payload for <c>POST /capture</c>. Mirrors the PostHog capture shape: either
/// a single event (top-level <c>event</c>/<c>distinct_id</c>) or a batch
/// (<c>batch</c> array). The API key may come from the body or the
/// <c>X-Api-Key</c> header.
/// </summary>
public record CaptureRequest
{
    [JsonPropertyName("api_key")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("distinct_id")]
    public string? DistinctId { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    [JsonPropertyName("properties")]
    public JsonElement? Properties { get; init; }

    [JsonPropertyName("batch")]
    public List<CaptureEventItem>? Batch { get; init; }
}

public record CaptureEventItem
{
    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("distinct_id")]
    public string? DistinctId { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    [JsonPropertyName("properties")]
    public JsonElement? Properties { get; init; }
}

public record CaptureResponse(string Status, int Ingested);
