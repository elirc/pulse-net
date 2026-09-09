using System.Security.Claims;

namespace Pulse.Api.Auth;

public static class RestrictedToken
{
    public const string ModeClaim = "pulse:token-mode";
    public const string ProjectClaim = "pulse:token-project";
    public const string ScopeClaim = "pulse:token-scope";
    public static readonly string[] KnownScopes = ["analytics:read", "configuration:read", "configuration:write", "exports:write"];
    public static bool IsRestricted(ClaimsPrincipal user) => user.HasClaim(ModeClaim, "restricted");
    public static Guid[] Projects(ClaimsPrincipal user) => user.FindAll(ProjectClaim).Select(c => Guid.TryParse(c.Value, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty).Distinct().ToArray();
    public static IResult? CheckProject(HttpContext http, Guid projectId)
    {
        if (!IsRestricted(http.User)) return null;
        if (!Projects(http.User).Contains(projectId)) return Results.NotFound();
        var scope = ProjectPermissionMatrix.Find(http)?.Scope;
        return scope is not null && http.User.HasClaim(ScopeClaim, scope) ? null
            : Results.Problem("This token does not grant the required capability.", statusCode: 403);
    }
}

/// <summary>Unclassified and global routes fail closed for restricted credentials.</summary>
public sealed class RestrictedTokenMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (!RestrictedToken.IsRestricted(http.User)) { await next(http); return; }
        var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var path = http.Request.Path.Value?.TrimEnd('/');
        if (HttpMethods.IsGet(http.Request.Method) && (string.Equals(path, "/api/auth/me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api/projects", StringComparison.OrdinalIgnoreCase))) { await next(http); return; }
        if (route?.StartsWith("/api/projects/{", StringComparison.OrdinalIgnoreCase) == true &&
            Guid.TryParse((http.Request.RouteValues["projectId"] ?? http.Request.RouteValues["id"])?.ToString(), out var projectId))
        {
            if (RestrictedToken.CheckProject(http, projectId) is { } denied) { await denied.ExecuteAsync(http); return; }
            if (await http.RequestServices.GetRequiredService<ProjectAccessService>().RequireMemberAsync(http, projectId, http.RequestAborted) is { } roleDenied)
            { await roleDenied.ExecuteAsync(http); return; }
            await next(http); return;
        }
        await Results.Problem("Restricted tokens cannot use this operation.", statusCode: 403).ExecuteAsync(http);
    }
}
