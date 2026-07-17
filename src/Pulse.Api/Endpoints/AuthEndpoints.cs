using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/register", async (
            RegisterRequest request,
            PulseDbContext db,
            JwtTokenIssuer tokens,
            CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var email = request.Email?.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            {
                errors["email"] = ["A valid email is required."];
            }

            if (request.Password is null || request.Password.Length < 8)
            {
                errors["password"] = ["Password must be at least 8 characters."];
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors["name"] = ["Name is required."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            if (await db.Users.AnyAsync(u => u.Email == email, ct))
            {
                return Results.Problem(
                    title: "Email already registered",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var user = new User
            {
                Email = email!,
                Name = request.Name!.Trim(),
                PasswordHash = PasswordHasher.Hash(request.Password!),
            };

            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/auth/me", ToAuthResponse(user, tokens));
        });

        auth.MapPost("/login", async (
            LoginRequest request,
            PulseDbContext db,
            JwtTokenIssuer tokens,
            CancellationToken ct) =>
        {
            var email = request.Email?.Trim().ToLowerInvariant();
            var user = string.IsNullOrWhiteSpace(email)
                ? null
                : await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);

            if (user is null
                || request.Password is null
                || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            {
                return Results.Problem(
                    title: "Invalid credentials",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(ToAuthResponse(user, tokens));
        });

        auth.MapGet("/me", async (HttpContext http, PulseDbContext db, CancellationToken ct) =>
        {
            var userId = ProjectAccessService.GetUserId(http.User);
            var user = userId is null ? null : await db.Users.FindAsync([userId.Value], ct);

            return user is null
                ? Results.NotFound()
                : Results.Ok(ToUserResponse(user));
        }).RequireAuthorization();

        var keys = app.MapGroup("/api/personal-api-keys").RequireAuthorization();

        keys.MapPost("/", async (
            CreatePersonalApiKeyRequest request,
            HttpContext http,
            PulseDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["Key name is required."],
                });
            }

            var userId = ProjectAccessService.GetUserId(http.User)!.Value;
            var plaintext = ApiKeyGenerator.NewPersonalKey();

            var key = new PersonalApiKey
            {
                UserId = userId,
                Name = request.Name.Trim(),
                KeyHash = ApiKeyGenerator.Sha256(plaintext),
                KeySuffix = plaintext[^4..],
            };

            db.PersonalApiKeys.Add(key);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/personal-api-keys/{key.Id}",
                new PersonalApiKeyCreatedResponse(key.Id, key.Name, plaintext, key.CreatedAt));
        });

        keys.MapGet("/", async (int? limit, int? offset, HttpContext http, PulseDbContext db, CancellationToken ct) =>
        {
            var userId = ProjectAccessService.GetUserId(http.User)!.Value;
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var skip = Math.Max(offset ?? 0, 0);

            var list = await db.PersonalApiKeys
                .Where(k => k.UserId == userId)
                .OrderBy(k => k.CreatedAt)
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);

            return Results.Ok(list.Select(k =>
                new PersonalApiKeyResponse(k.Id, k.Name, k.KeySuffix, k.CreatedAt)));
        });

        keys.MapDelete("/{keyId:guid}", async (
            Guid keyId,
            HttpContext http,
            PulseDbContext db,
            CancellationToken ct) =>
        {
            var userId = ProjectAccessService.GetUserId(http.User)!.Value;

            var key = await db.PersonalApiKeys
                .SingleOrDefaultAsync(k => k.Id == keyId && k.UserId == userId, ct);

            if (key is null)
            {
                return Results.NotFound();
            }

            db.PersonalApiKeys.Remove(key);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        return app;
    }

    private static AuthResponse ToAuthResponse(User user, JwtTokenIssuer tokens)
    {
        var (token, expiresAt) = tokens.Issue(user);
        return new AuthResponse(token, expiresAt, ToUserResponse(user));
    }

    private static UserResponse ToUserResponse(User user) =>
        new(user.Id, user.Email, user.Name, user.CreatedAt);
}
