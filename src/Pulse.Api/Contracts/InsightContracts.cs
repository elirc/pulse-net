using System.Text.Json;

namespace Pulse.Api.Contracts;

public record FunnelRequest(
    List<string>? Steps,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? WindowDays,
    List<PropertyFilterDto>? Filters);

public record SaveInsightRequest(string? Name, string? Type, JsonElement? Config);

public record InsightResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Type,
    JsonElement Config,
    DateTimeOffset CreatedAt);
