using Pulse.Api.Auth;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static class ProjectDiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapProjectDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}");
        group.MapGet("/event-usage", async (Guid projectId, DateTimeOffset? from, DateTimeOffset? to, int? limit,
            HttpContext http, ProjectAccessService access, ProjectDiscoveryService discovery, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (!ValidRange(from, to, 90)) return InputRules.Problem("from", "Supply a nonempty half-open from/to range of at most 90 days.");
            var take = limit ?? 20;
            if (take is < 1 or > 100) return InputRules.Problem("limit", "Limit must be between 1 and 100.");
            return Results.Ok(await discovery.EventUsageAsync(projectId, from!.Value.ToUniversalTime(), to!.Value.ToUniversalTime(), take, ct));
        });
        group.MapGet("/property-values", async (Guid projectId, string? @event, string? property, DateTimeOffset? from, DateTimeOffset? to, int? limit,
            HttpContext http, ProjectAccessService access, ProjectDiscoveryService discovery, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (InputRules.Text(@event, 200, "event", out var name, required: true) is { } invalid) return invalid;
            if (InputRules.Text(property, 200, "property", out var propertyName, required: true) is { } badProperty) return badProperty;
            if (!ValidRange(from, to, 31)) return InputRules.Problem("from", "Supply a nonempty half-open from/to range of at most 31 days.");
            var take = limit ?? 10;
            if (take is < 1 or > 25) return InputRules.Problem("limit", "Limit must be between 1 and 25.");
            var result = await discovery.PropertyValuesAsync(projectId, name!, propertyName!, from!.Value.ToUniversalTime(), to!.Value.ToUniversalTime(), take, ct);
            return result.Status == 200 ? Results.Ok(result.Result) : Results.Problem(result.Detail, statusCode: result.Status);
        });
        group.MapGet("/overview", async (Guid projectId, HttpContext http, ProjectAccessService access,
            ProjectDiscoveryService discovery, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var result = await discovery.OverviewAsync(projectId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        group.MapGet("/ingestion/status", async (Guid projectId, int? maxPendingAgeSeconds, int? maxPending,
            HttpContext http, ProjectAccessService access, IngestionOperationsService ingestion, TimeProvider clock, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var thresholds = new IngestionThresholds(maxPendingAgeSeconds ?? 60, maxPending ?? 1000);
            if (thresholds.MaxPendingAgeSeconds is < 1 or > 3600) return InputRules.Problem("maxPendingAgeSeconds", "Use a value from 1 to 3,600 seconds.");
            if (thresholds.MaxPending is < 1 or > 100000) return InputRules.Problem("maxPending", "Use a value from 1 to 100,000 pending rows.");
            var now = clock.GetUtcNow();
            return Results.Ok(IngestionStatusPolicy.Classify(await ingestion.GetMetricsAsync(projectId, now, ct), thresholds, now));
        });
        return app;
    }

    private static bool ValidRange(DateTimeOffset? from, DateTimeOffset? to, int days) =>
        from is not null && to is not null && from < to && (to.Value - from.Value).TotalDays <= days;
}
