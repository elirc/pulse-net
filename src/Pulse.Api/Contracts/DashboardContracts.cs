using System.Text.Json;

namespace Pulse.Api.Contracts;

public record CreateDashboardRequest(string? Name, string? Description);

public record UpdateDashboardRequest(string? Name, string? Description);

public record AddTileRequest(Guid? InsightId, JsonElement? Layout);

public record UpdateTileRequest(JsonElement? Layout);

public record DashboardTileResponse(
    Guid Id,
    Guid InsightId,
    string InsightName,
    string InsightType,
    JsonElement Layout,
    DateTimeOffset CreatedAt);

public record DashboardResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Description,
    IReadOnlyList<DashboardTileResponse> Tiles,
    DateTimeOffset CreatedAt);

public record DashboardSummaryResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Description,
    int TileCount,
    DateTimeOffset CreatedAt);

public record TileRefreshResponse(
    Guid TileId,
    Guid InsightId,
    string InsightName,
    string InsightType,
    JsonElement Layout,
    object? Result,
    string? Error);

public record DashboardRefreshResponse(
    Guid DashboardId,
    string Name,
    DateTimeOffset RefreshedAt,
    IReadOnlyList<TileRefreshResponse> Tiles);
