using System.Security.Claims;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Auth;

public static class AuthenticatedActor
{
    public const string PersonalKeyIdClaim = "pulse:personal-key-id";
    public static AuditActor From(HttpContext http) => new(ProjectAccessService.GetUserId(http.User)
        ?? throw new InvalidOperationException("An authenticated user is required before creating audit context."),
        Guid.TryParse(http.User.FindFirstValue(PersonalKeyIdClaim), out var keyId) ? keyId : null);
}
