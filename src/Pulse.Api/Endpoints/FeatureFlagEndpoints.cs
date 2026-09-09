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
            PulseDbContext db,
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

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await ProjectMaintenance.AssertWritableAsync(db, project.Id, ct);
            var values = await flags.EvaluateAllAsync(project.Id, request.DistinctId.Trim(), ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new DecideResponse(values));
        });

        var group = app.MapGroup("/api/projects/{projectId:guid}/feature-flags");
        MapFlagDiagnosticFeatures(group);
        MapFlagGovernanceFeatures(group);

        group.MapPost("/", async (
            Guid projectId,
            CreateFeatureFlagRequest request,
            HttpContext http,
            PulseDbContext db,
            FlagMutationService mutations,
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

            if (!Enum.TryParse<FeatureFlagType>(request.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type))
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

            var created = await mutations.CreateAsync(flag, AuthenticatedActor.From(http), ct);
            if (created.Status != 201) return FlagFailure(created.Status);
            http.Response.Headers.ETag = FlagPrecondition.ETag(flag);

            return Results.Created(
                $"/api/projects/{projectId}/feature-flags/{flag.Key}", ToResponse(flag));
        });

        group.MapGet("/", async (
            Guid projectId,
            int? limit,
            int? offset,
            bool? active,
            string? keyPrefix,
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

            if (InputRules.Text(keyPrefix, 200, "keyPrefix", out var prefix) is { } invalid) return invalid;
            prefix = prefix?.ToLowerInvariant();

            var flags = await db.FeatureFlags
                .Where(f => f.ProjectId == projectId)
                .Where(f => active == null || f.Active == active)
                .Where(f => prefix == null || f.Key.ToLower().StartsWith(prefix))
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

            if (flag is null) return Results.NotFound();
            http.Response.Headers.ETag = FlagPrecondition.ETag(flag);
            return Results.Ok(ToResponse(flag));
        });

        group.MapPut("/{key}", async (
            Guid projectId,
            string key,
            UpdateFeatureFlagRequest request,
            HttpContext http,
            PulseDbContext db,
            FlagMutationService mutations,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var flag = await db.FeatureFlags.AsNoTracking()
                .SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct);

            if (flag is null)
            {
                return Results.NotFound();
            }

            if (FlagPrecondition.Require(http, flag, out var expectedRevision) is { } precondition) return precondition;

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

            var updated = await mutations.UpdateAsync(flag, expectedRevision, AuthenticatedActor.From(http), ct);
            if (updated.Status != 200) return FlagFailure(updated.Status);
            http.Response.Headers.ETag = FlagPrecondition.ETag(flag);

            return Results.Ok(ToResponse(flag));
        });

        group.MapDelete("/{key}", async (
            Guid projectId,
            string key,
            HttpContext http,
            PulseDbContext db,
            FlagMutationService mutations,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var flag = await db.FeatureFlags.AsNoTracking()
                .SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct);

            if (flag is null)
            {
                return Results.NotFound();
            }

            if (FlagPrecondition.Require(http, flag, out var expectedRevision) is { } precondition) return precondition;
            var status = await mutations.DeleteAsync(flag, expectedRevision, AuthenticatedActor.From(http), ct);
            return status == 204 ? Results.NoContent() : FlagFailure(status);
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
        return FlagConfigurationValidator.TryValidate(rolloutPercentage, filters, variants, type, errors, out rollout, out filtersJson, out variantsJson);
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
            flag.CreatedAt,
            flag.Revision);

    private static IResult FlagFailure(int status) => status == 404 ? Results.NotFound() : Results.Problem(statusCode: status,
        detail: status switch { 412 => "The flag changed. Fetch current state and reconcile your edit.", 409 => "The requested change conflicts with current state.",
            400 => "The configuration exceeds the supported size or shape.", _ => "Current project permissions do not permit this change." });
}
