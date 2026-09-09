using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static class BoundedAnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapBoundedAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:guid}/insights/trend-bounded", TrendAsync);
        app.MapGet("/api/projects/{projectId:guid}/persons/{personId:guid}/sessions", SessionsAsync);
        return app;
    }

    private static async Task<IResult> TrendAsync(Guid projectId, string? @event, DateTimeOffset? from,
        DateTimeOffset? to, string? interval, string? filters, string? breakdown,
        HttpContext http, ProjectAccessService access, BoundedQueryService queries, CancellationToken ct)
    {
        if (await access.RequireReadAsync(http, projectId, ct) is { } denied) return denied;
        if (InputRules.Text(@event, 200, "event", out var eventName) is { } invalidEvent) return invalidEvent;
        if (from is null || to is null) return Invalid("from", "Explicit from and to query parameters are required.");
        if (from >= to || to.Value - from.Value > TimeSpan.FromDays(90))
            return Invalid("from", "Supply a nonempty range of at most 90 days; trend boundaries are inclusive.");
        if (interval?.ToLowerInvariant() is not ("hour" or "day" or "week")
            || !Enum.TryParse<TrendInterval>(interval, true, out var parsedInterval))
            return Invalid("interval", "Interval must be one of: hour, day, week.");
        if (!string.IsNullOrWhiteSpace(breakdown))
            return Invalid("breakdown", "Breakdown is not supported by the bounded trend route.");
        if (!FilterParsing.TryParseJson(filters, out var parsedFilters, out var filterError))
            return Invalid("filters", filterError);
        if (parsedFilters.Any(f => f.Target != FilterTarget.Event))
            return Invalid("filters", "Only event-targeted filters are supported by the bounded trend route.");

        try
        {
            return Results.Ok(await queries.TrendAsync(projectId, eventName!, from.Value.ToUniversalTime(),
                to.Value.ToUniversalTime(), parsedInterval, parsedFilters, ct));
        }
        catch (QueryBudgetExceededException ex) { return BudgetFailure(ex); }
        catch (QueryDeadlineExceededException ex)
        {
            return Results.Problem(title: "Query deadline exceeded", detail: ex.Message,
                statusCode: StatusCodes.Status504GatewayTimeout, extensions: new Dictionary<string, object?> { ["reason"] = "deadline" });
        }
    }

    private static async Task<IResult> SessionsAsync(Guid projectId, Guid personId, DateTimeOffset? from,
        DateTimeOffset? to, int? gapMinutes, HttpContext http, ProjectAccessService access,
        PersonSessionService sessions, CancellationToken ct)
    {
        if (await access.RequireRoleAsync(http, projectId, Pulse.Domain.Entities.ProjectRole.Viewer, ct) is { } denied) return denied;
        if (from is null || to is null || from >= to || to.Value - from.Value > TimeSpan.FromDays(7))
            return Invalid("from", "Supply a nonempty half-open from/to range of at most 7 days.");
        var gap = gapMinutes ?? 30;
        if (gap is < 1 or > 120) return Invalid("gapMinutes", "gapMinutes must be between 1 and 120.");
        try
        {
            var result = await sessions.QueryAsync(projectId, personId, from.Value.ToUniversalTime(),
                to.Value.ToUniversalTime(), gap, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (QueryBudgetExceededException ex) { return BudgetFailure(ex); }
        catch (QueryDeadlineExceededException ex)
        {
            return Results.Problem(title: "Query deadline exceeded", detail: ex.Message,
                statusCode: StatusCodes.Status504GatewayTimeout, extensions: new Dictionary<string, object?> { ["reason"] = "deadline" });
        }
    }

    private static IResult Invalid(string key, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });

    private static IResult BudgetFailure(QueryBudgetExceededException ex) => Results.Problem(
        title: "Query work budget exceeded", detail: ex.Guidance,
        statusCode: StatusCodes.Status422UnprocessableEntity,
        extensions: new Dictionary<string, object?> { ["reason"] = ToReason(ex.Reason) });

    private static string ToReason(QueryBudgetReason reason) => reason switch
    {
        QueryBudgetReason.RowLimit => "row_limit",
        QueryBudgetReason.ByteLimit => "byte_limit",
        QueryBudgetReason.BucketLimit => "bucket_limit",
        QueryBudgetReason.OutputLimit => "session_limit",
        QueryBudgetReason.AnnotationLimit => "annotation_limit",
        _ => "budget_limit",
    };
}
