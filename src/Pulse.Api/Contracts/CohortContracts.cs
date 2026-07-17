using System.Text.Json;

namespace Pulse.Api.Contracts;

/// <summary>
/// <c>type</c> is <c>static</c> (with optional <c>personIds</c>) or
/// <c>dynamic</c> (with a <c>rules</c> array, see <c>CohortRuleParser</c>).
/// </summary>
public record CreateCohortRequest(
    string? Name,
    string? Type,
    List<Guid>? PersonIds,
    JsonElement? Rules);

public record CohortResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Type,
    JsonElement Rules,
    DateTimeOffset CreatedAt);

public record CohortMembersResponse(Guid CohortId, int Count, IReadOnlyList<Guid> PersonIds);

public record ModifyCohortMembersRequest(List<Guid>? PersonIds);
