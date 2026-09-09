using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static partial class FeatureFlagEndpoints
{
    private static void MapFlagDiagnosticFeatures(RouteGroupBuilder group)
    {
        group.MapPost("/{key}/clone", async (Guid projectId, string key, CloneFlagRequest request, HttpContext http,
            ProjectAccessService access, FlagDraftService drafts, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (InputRules.Text(request.Key, 200, "key", out var target, required: true) is { } invalid) return invalid;
            if (!KeyPattern().IsMatch(target!)) return InputRules.Problem("key", "Use ASCII letters, digits, hyphen, or underscore.");
            if (InputRules.Text(request.Name, 200, "name", out var name, required: true) is { } badName) return badName;
            var result = await drafts.CloneAsync(projectId, key, target!, name!, AuthenticatedActor.From(http), ct);
            if (result.Flag is { } created) http.Response.Headers.ETag = FlagPrecondition.ETag(created);
            return result.Status switch
            {
                201 => Results.Created($"/api/projects/{projectId}/feature-flags/{result.Flag!.Key}", ToResponse(result.Flag)),
                404 => Results.NotFound(),
                _ => FlagFailure(result.Status),
            };
        });

        group.MapPost("/{key}/explain", async (Guid projectId, string key, ExplainFlagRequest request,
            HttpContext http, ProjectAccessService access, FeatureFlagService flags, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (InputRules.Text(request.DistinctId, 400, "distinctId", out var identity, required: true) is { } invalid) return invalid;
            var result = await flags.ExplainAsync(projectId, key, identity!, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPost("/evaluate-batch", async (Guid projectId, EvaluateFlagBatchRequest request,
            HttpContext http, ProjectAccessService access, FeatureFlagService flags, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (ValidateFlagInputs(request.DistinctIds, 50, 400, "distinctIds", out var identities) is { } invalid) return invalid;
            if (ValidateFlagInputs(request.Keys, 20, 200, "keys", out var keys) is { } badKeys) return badKeys;
            var result = await flags.EvaluateBatchAsync(projectId, identities, keys, ct);
            return result is null ? InputRules.Problem("keys", "One or more selected keys are unavailable in this project.") : Results.Ok(result);
        });
    }

    private static IResult? ValidateFlagInputs(List<string>? input, int maxCount, int maxLength, string field, out List<string> values)
    {
        values = [];
        if (input is null || input.Count < 1 || input.Count > maxCount) return InputRules.Problem(field, $"Supply between 1 and {maxCount} values.");
        foreach (var value in input)
        {
            if (InputRules.Text(value, maxLength, field, out var normalized, required: true) is { } invalid) return invalid;
            if (values.Contains(normalized!, StringComparer.Ordinal)) return InputRules.Problem(field, "Values must be unique after trimming.");
            values.Add(normalized!);
        }
        return null;
    }
}
