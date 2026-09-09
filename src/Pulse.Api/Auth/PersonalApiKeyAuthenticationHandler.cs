using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Api.Auth;

/// <summary>
/// Authenticates <c>Authorization: Bearer pk_user_…</c> personal API keys by
/// looking up the key's SHA-256 hash. Produces the same claims shape as a JWT
/// session, so downstream authorization is credential-agnostic.
/// </summary>
public class PersonalApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "PersonalApiKey";

    public PersonalApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var key = header["Bearer ".Length..].Trim();
        if (!key.StartsWith(ApiKeyGenerator.PersonalPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var db = Context.RequestServices.GetRequiredService<PulseDbContext>();
        var hash = ApiKeyGenerator.Sha256(key);
        var now = Context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();

        var account = await (
            from k in db.PersonalApiKeys
            join u in db.Users on k.UserId equals u.Id
            where k.KeyHash == hash && (k.ExpiresAt == null || k.ExpiresAt > now)
            select new { User = u, KeyId = k.Id, k.Mode, k.ExpiresAt }).SingleOrDefaultAsync(Context.RequestAborted);

        if (account is null)
        {
            return AuthenticateResult.Fail("Unknown personal API key.");
        }

        var user = account.User;
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(AuthenticatedActor.PersonalKeyIdClaim, account.KeyId.ToString()),
        ], SchemeName);
        if (account.Mode == PersonalKeyMode.Restricted)
        {
            if (account.ExpiresAt is null) return AuthenticateResult.Fail("Restricted tokens require an expiry.");
            identity.AddClaim(new(RestrictedToken.ModeClaim, "restricted"));
            var projects = await db.PersonalKeyProjects.Where(p => p.KeyId == account.KeyId).Select(p => p.ProjectId).Take(21).ToListAsync(Context.RequestAborted);
            var scopes = await db.PersonalKeyScopes.Where(s => s.KeyId == account.KeyId).Select(s => s.Scope).Take(5).ToListAsync(Context.RequestAborted);
            if (projects.Count is < 1 or > 20 || scopes.Count is < 1 or > 4 || scopes.Any(s => !RestrictedToken.KnownScopes.Contains(s, StringComparer.Ordinal)))
                return AuthenticateResult.Fail("Invalid restricted token configuration.");
            identity.AddClaims(projects.Select(id => new Claim(RestrictedToken.ProjectClaim, id.ToString())));
            identity.AddClaims(scopes.Select(scope => new Claim(RestrictedToken.ScopeClaim, scope)));
        }
        else if (account.Mode != PersonalKeyMode.LegacyUnrestricted) return AuthenticateResult.Fail("Unknown token mode.");

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
