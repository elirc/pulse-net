using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pulse.Domain.Entities;

namespace Pulse.Api.Auth;

/// <summary>Creates signed HS256 JWTs for management-API sessions.</summary>
public class JwtTokenIssuer
{
    public const string DefaultIssuer = "pulse-net";

    private readonly IConfiguration _config;
    private readonly TimeProvider _clock;

    public JwtTokenIssuer(IConfiguration config, TimeProvider clock)
    {
        _config = config;
        _clock = clock;
    }

    public (string Token, DateTimeOffset ExpiresAt) Issue(User user)
    {
        var secret = _config["Jwt:Secret"]
                     ?? throw new InvalidOperationException("Jwt:Secret is not configured.");
        var lifetimeMinutes = _config.GetValue("Jwt:LifetimeMinutes", 480);
        var expiresAt = _clock.GetUtcNow().AddMinutes(lifetimeMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _config["Jwt:Issuer"] ?? DefaultIssuer,
            Audience = _config["Jwt:Audience"] ?? DefaultIssuer,
            Expires = expiresAt.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Name, user.Name),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                SecurityAlgorithms.HmacSha256),
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return (token, expiresAt);
    }
}
