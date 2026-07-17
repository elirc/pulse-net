using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Pulse.Infrastructure;

namespace Pulse.Api.Auth;

/// <summary>
/// Project-level authorization. Management access requires membership;
/// query-only access is also granted by the project's <c>rk_live_…</c> read
/// key via the <c>X-Api-Key</c> header. Non-members get 404 (not 403) so
/// project ids don't leak.
/// </summary>
public class ProjectAccessService
{
    private readonly PulseDbContext _db;

    public ProjectAccessService(PulseDbContext db)
    {
        _db = db;
    }

    /// <summary>The authenticated user's id, from JWT or personal-key claims.</summary>
    public static Guid? GetUserId(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);

        return Guid.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// Requires the caller to be a member of the project. Returns null when
    /// access is granted, otherwise the error result to short-circuit with.
    /// </summary>
    public async Task<IResult?> RequireMemberAsync(HttpContext http, Guid projectId, CancellationToken ct)
    {
        var userId = GetUserId(http.User);
        if (userId is null)
        {
            return Results.Problem(
                title: "Authentication required",
                detail: "Provide a JWT or personal API key as a Bearer token.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var isMember = await _db.ProjectMemberships
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);

        return isMember ? null : Results.NotFound();
    }

    /// <summary>
    /// Requires read access to the project: either membership, or the
    /// project's read key in <c>X-Api-Key</c>. Write keys are rejected —
    /// they only authenticate <c>POST /capture</c>.
    /// </summary>
    public async Task<IResult?> RequireReadAsync(HttpContext http, Guid projectId, CancellationToken ct)
    {
        var apiKey = http.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(apiKey) && GetUserId(http.User) is null)
        {
            var matches = await _db.Projects
                .AnyAsync(p => p.Id == projectId && p.ReadKey == apiKey, ct);

            return matches
                ? null
                : Results.Problem(
                    title: "Invalid read key",
                    detail: "Only the project's rk_live_ read key grants query access.",
                    statusCode: StatusCodes.Status401Unauthorized);
        }

        return await RequireMemberAsync(http, projectId, ct);
    }
}
