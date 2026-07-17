using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pulse.Domain;
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

        var user = await (
            from k in db.PersonalApiKeys
            join u in db.Users on k.UserId equals u.Id
            where k.KeyHash == hash
            select u).SingleOrDefaultAsync(Context.RequestAborted);

        if (user is null)
        {
            return AuthenticateResult.Fail("Unknown personal API key.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Name),
        ], SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
