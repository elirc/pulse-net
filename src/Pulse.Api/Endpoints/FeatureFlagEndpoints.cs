using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static partial class FeatureFlagEndpoints
{
    [GeneratedRegex("^[a-zA-Z0-9_-]+$")]
    private static partial Regex KeyPattern();

    public static IEndpointRouteBuilder MapFeatureFlagEndpoints(this IEndpointRouteBuilder app)
    {
        // SDK-facing evaluation, authenticated by the project write key like /capture.
        app.MapPost("/decide", async (
            DecideRequest request,
            HttpContext http,
            CaptureService capture,
            FeatureFlagService flags,
            CancellationToken ct) =>
        {
            var apiKey = request.ApiKey
                         ?? http.Request.Headers["X-Api-Key"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Results.Problem(
                    title: "Missing API key",
                    detail: "Provide api_key in the body or the X-Api-Key header.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var project = await capture.FindProjectByApiKeyAsync(apiKey, ct);
            if (project is null)
            {
                return Results.Problem(
                    title: "Invalid API key",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (string.IsNullOrWhiteSpace(request.DistinctId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["distinct_id"] = ["distinct_id is required."],
                });
            }

            var values = await flags.EvaluateAllAsync(project.Id, request.DistinctId.Trim(), ct);
            return Results.Ok(new DecideResponse(values));
        });

        var group = app.MapGroup("/api/projects/{projectId:guid}/feature-flags");

        group.MapPost("/", async (
            Guid projectId,
            CreateFeatureFlagRequest request,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var errors = new Dictionary<string, string[]>();

            var key = request.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key) || !KeyPattern().IsMatch(key))
            {
                errors["key"] = ["Flag key is required and may only contain letters, digits, '-' and '_'."];
            }

            if (!Enum.TryParse<FeatureFlagType>(request.Type, ignoreCase: true, out var type))
            {
                errors["type"] = ["Type must be 'boolean' or 'multivariate'."];
            }

            if (!TryValidateShared(request.RolloutPercentage, request.Filters, request.Variants,
                    type, errors, out var rollout, out var filtersJson, out var variantsJson))
            {
                // Errors were recorded by the helper.
            }

            if (type == FeatureFlagType.Multivariate && variantsJson == "[]" && errors.Count == 0)
            {
                errors["variants"] = ["Multivariate flags need a 'variants' array summing to 100."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            if (await db.FeatureFlags.AnyAsync(f => f.ProjectId == projectId && f.Key == key, ct))
            {
                return Results.Problem(
                    title: "Flag key already exists",
                    detail: $"The project already has a flag with key '{key}'.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var flag = new FeatureFlag
            {
                ProjectId = projectId,
                Key = key!,
                Name = request.Name?.Trim() ?? string.Empty,
                Type = type,
                Active = request.Active ?? true,
                RolloutPercentage = rollout,
                FiltersJson = filtersJson,
                VariantsJson = variantsJson,
            };

            db.FeatureFlags.Add(flag);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/projects/{projectId}/feature-flags/{flag.Key}", ToResponse(flag));
        });

        group.MapGet("/", async (
            Guid projectId,
            int? limit,
            int? offset,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var skip = Math.Max(offset ?? 0, 0);

            var flags = await db.FeatureFlags
                .Where(f => f.ProjectId == projectId)
                .OrderBy(f => f.Key)
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);

            return Results.Ok(flags.Select(ToResponse));
        });

        // Local-evaluation payload: full definitions so SDKs can evaluate
        // without a /decide round-trip per user. Read-key friendly.
        group.MapGet("/local-evaluation", async (
            Guid projectId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireReadAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var flags = await db.FeatureFlags
                .Where(f => f.ProjectId == projectId)
                .OrderBy(f => f.Key)
                .ToListAsync(ct);

            return Results.Ok(new LocalEvaluationResponse(flags.Select(ToResponse).ToList()));
        });

        group.MapGet("/{key}", async (
            Guid projectId,
            string key,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var flag = await db.FeatureFlags
                .SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct);

            return flag is null ? Results.NotFound() : Results.Ok(ToResponse(flag));
        });

        group.MapPut("/{key}", async (
            Guid projectId,
            string key,
            UpdateFeatureFlagRequest request,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var flag = await db.FeatureFlags
                .SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct);

            if (flag is null)
            {
                return Results.NotFound();
            }

            var errors = new Dictionary<string, string[]>();
            if (!TryValidateShared(request.RolloutPercentage, request.Filters, request.Variants,
                    flag.Type, errors, out var rollout, out var filtersJson, out var variantsJson))
            {
                // Errors were recorded by the helper.
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            flag.Name = request.Name?.Trim() ?? flag.Name;
            flag.Active = request.Active ?? flag.Active;
            flag.RolloutPercentage = request.RolloutPercentage is null ? flag.RolloutPercentage : rollout;
            flag.FiltersJson = request.Filters is null ? flag.FiltersJson : filtersJson;
            flag.VariantsJson = request.Variants is null ? flag.VariantsJson : variantsJson;

            await db.SaveChangesAsync(ct);

            return Results.Ok(ToResponse(flag));
        });

        group.MapDelete("/{key}", async (
            Guid projectId,
            string key,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var flag = await db.FeatureFlags
                .SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct);

            if (flag is null)
            {
                return Results.NotFound();
            }

            db.FeatureFlags.Remove(flag);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        return app;
    }

    /// <summary>
    /// Validates rollout, targeting filters and variants; returns their
    /// normalized stored forms. Adds field errors to <paramref name="errors"/>.
    /// </summary>
    private static bool TryValidateShared(
        double? rolloutPercentage,
        JsonElement? filters,
        JsonElement? variants,
        FeatureFlagType type,
        Dictionary<string, string[]> errors,
        out double rollout,
        out string filtersJson,
        out string variantsJson)
    {
        rollout = rolloutPercentage ?? 100;
        if (rollout is < 0 or > 100)
        {
            errors["rolloutPercentage"] = ["rolloutPercentage must be between 0 and 100."];
        }

        filtersJson = filters is { ValueKind: JsonValueKind.Array } f ? f.GetRawText() : "[]";
        if (!PropertyFilterParser.TryParse(filtersJson, out _, out var filterError, allowEventTarget: false))
        {
            errors["filters"] = [filterError];
        }

        variantsJson = variants is { ValueKind: JsonValueKind.Array } v ? v.GetRawText() : "[]";
        if (type == FeatureFlagType.Multivariate)
        {
            if (!FlagVariantParser.TryParse(variantsJson, out _, out var variantError))
            {
                errors["variants"] = [variantError];
            }
        }
        else if (variantsJson != "[]")
        {
            errors["variants"] = ["Boolean flags cannot have variants."];
        }

        return errors.Count == 0;
    }

    private static FeatureFlagResponse ToResponse(FeatureFlag flag) =>
        new(
            flag.Id,
            flag.ProjectId,
            flag.Key,
            flag.Name,
            flag.Type.ToString().ToLowerInvariant(),
            flag.Active,
            flag.RolloutPercentage,
            JsonSerializer.Deserialize<JsonElement>(flag.FiltersJson),
            JsonSerializer.Deserialize<JsonElement>(flag.VariantsJson),
            flag.CreatedAt);
}
