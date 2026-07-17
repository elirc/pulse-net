using System.Text.Json;

namespace Pulse.Api.Contracts;

/// <summary>
/// Creates an async export job. <c>type</c>: <c>events</c> (event/from/to/filters),
/// <c>persons</c>, or <c>insight</c> (insightId). <c>format</c>: <c>csv</c> or <c>json</c>.
/// </summary>
public record CreateExportJobRequest(
    string? Type,
    string? Format,
    string? Event,
    DateTimeOffset? From,
    DateTimeOffset? To,
    JsonElement? Filters,
    Guid? InsightId);

public record ExportJobResponse(
    Guid Id,
    Guid ProjectId,
    string Type,
    string Format,
    string Status,
    int RowCount,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
