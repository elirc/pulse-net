using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Api.Auth;

public static class ProjectMaintenanceAccess
{
    public static bool AllowedDuringMaintenance(HttpContext http)
    {
        var raw = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (raw is null) return false;
        var route = ProjectPermissionMatrix.Normalize(http.Request.Method, raw);
        return route.Contains("/erasure-jobs", StringComparison.OrdinalIgnoreCase) ||
            route is "POST /api/projects/{}/persons/{}/erasure" or "DELETE /api/projects/{}/persons/{}" or "GET /api/projects/{}/ingestion/status";
    }
    public static async Task<IResult?> DenialAsync(HttpContext http, Guid projectId, PulseDbContext db, CancellationToken ct)
    {
        if (AllowedDuringMaintenance(http)) return null;
        var state = await db.ProjectIngestionStates.AsNoTracking().Where(s => s.ProjectId == projectId).Select(s => new { s.Paused, s.MaintenanceGeneration }).SingleOrDefaultAsync(ct);
        if (state?.Paused == true) return Results.Problem("Project data is temporarily unavailable during erasure.", statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "project_maintenance" });
        if (ProjectPermissionMatrix.Find(http)?.MinimumRole >= ProjectRole.Editor || http.Request.Method is not ("GET" or "POST"))
        {
            db.GuardedProjectId ??= projectId;
            db.GuardedMaintenanceGeneration ??= state?.MaintenanceGeneration ?? 0;
        }
        return null;
    }
}

/// <summary>
/// A relevant project read observes one database view and holds SQLite's reserved writer lock, so erasure cannot
/// commit its pause halfway through that view even when the database uses WAL. Because SQLite permits only one
/// reserved writer, these protected reads serialize with one another as well as with writers; keep their handlers
/// bounded and avoid external work inside them.
/// </summary>
public sealed class ProjectReadSnapshotFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext; var rule = ProjectPermissionMatrix.Find(http);
        // Capture preview only echoes caller-supplied input; it does not read stored person data.
        if (http.Request.Method is not ("GET" or "POST") || rule?.MinimumRole != ProjectRole.Viewer || ProjectMaintenanceAccess.AllowedDuringMaintenance(http) ||
            http.Request.Path.Value?.EndsWith("/capture/validate", StringComparison.OrdinalIgnoreCase) == true) return await next(context);
        var db = http.RequestServices.GetRequiredService<PulseDbContext>();
        if (db.Database.CurrentTransaction is not null) return await next(context);
        await db.Database.OpenConnectionAsync(http.RequestAborted);
        try
        {
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var participating = await db.Database.UseTransactionAsync(transaction, http.RequestAborted);
            var result = await next(context);
            await transaction.CommitAsync(http.RequestAborted);
            return result;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
