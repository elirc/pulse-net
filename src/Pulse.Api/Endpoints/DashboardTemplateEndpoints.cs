using System.Text.Json;
using Pulse.Api.Auth;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static partial class DashboardEndpoints
{
    private static void MapDashboardTemplateFeatures(RouteGroupBuilder group)
    {
        group.MapGet("/{dashboardId:guid}/template", async (Guid projectId, Guid dashboardId,
            HttpContext http, ProjectAccessService access, DashboardTemplateService templates, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var result = await templates.ExportAsync(projectId, dashboardId, ct);
            if (result.Missing) return Results.NotFound();
            if (result.Error is { } error) return Results.Problem(error, statusCode: 409);
            return Results.Ok(result.Template);
        });
        group.MapPost("/import", async (Guid projectId, HttpContext http, ProjectAccessService access,
            DashboardTemplateService templates, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var body = await LimitedJsonBody.ReadAsync(http.Request, DashboardTemplateService.MaximumBodyBytes, ct);
            if (body.Error is { } error) return error;
            DashboardTemplate? document;
            try { document = body.Value!.Value.Deserialize<DashboardTemplate>(JsonSerializerOptions.Web); }
            catch (JsonException) { return InputRules.Problem("body", "Body must match the version 1 template shape."); }
            if (document is null) return InputRules.Problem("body", "A template object is required.");
            var result = await templates.ImportAsync(projectId, document, ct);
            if (result.Error is { } invalid) return InputRules.Problem("template", invalid);
            var dashboard = result.Dashboard!;
            return Results.Created($"/api/projects/{projectId}/dashboards/{dashboard.Id}",
                ToResponse(dashboard, await LoadTilesAsync(db, dashboard.Id, ct)));
        });
    }
}
