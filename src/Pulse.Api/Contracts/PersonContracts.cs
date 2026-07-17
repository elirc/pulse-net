using System.Text.Json;

namespace Pulse.Api.Contracts;

public record PersonResponse(
    Guid Id,
    Guid ProjectId,
    JsonElement Properties,
    IReadOnlyList<string> DistinctIds,
    DateTimeOffset CreatedAt);
