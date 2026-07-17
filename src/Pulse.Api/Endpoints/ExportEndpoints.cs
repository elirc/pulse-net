using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static class ExportEndpoints
{
    private const string NextCursorHeader = "X-Next-Cursor";

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}");

        // --- Synchronous, cursor-paginated exports ---------------------------

        group.MapGet("/export/events", async (
            Guid projectId,
            string? format,
            string? @event,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? filters,
            string? cursor,
            int? limit,
            HttpContext http,
            ExportService exports,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            if (!TryParseFormat(format, out var parsedFormat))
            {
                return FormatProblem();
            }

            if (!FilterParsing.TryParseJson(filters, out var parsedFilters, out var filterError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["filters"] = [filterError],
                });
            }

            var take = Math.Clamp(limit ?? 100, 1, ExportService.MaxPageSize);

            EventExportPage page;
            try
            {
                page = await exports.EventsPageAsync(
                    projectId, @event?.Trim(), from, to, parsedFilters, cursor, take, ct);
            }
            catch (ArgumentException)
            {
                return CursorProblem();
            }

            if (page.NextCursor is not null)
            {
                http.Response.Headers[NextCursorHeader] = page.NextCursor;
            }

            return parsedFormat == "csv"
                ? Results.Text(ExportService.EventsCsv(page.Events), "text/csv")
                : Results.Ok(new { events = page.Events, nextCursor = page.NextCursor });
        });

        group.MapGet("/export/persons", async (
            Guid projectId,
            string? format,
            string? cursor,
            int? limit,
            HttpContext http,
            ExportService exports,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            if (!TryParseFormat(format, out var parsedFormat))
            {
                return FormatProblem();
            }

            var take = Math.Clamp(limit ?? 100, 1, ExportService.MaxPageSize);

            PersonExportPage page;
            try
            {
                page = await exports.PersonsPageAsync(projectId, cursor, take, ct);
            }
            catch (ArgumentException)
            {
                return CursorProblem();
            }

            if (page.NextCursor is not null)
            {
                http.Response.Headers[NextCursorHeader] = page.NextCursor;
            }

            return parsedFormat == "csv"
                ? Results.Text(ExportService.PersonsCsv(page.Persons), "text/csv")
                : Results.Ok(new { persons = page.Persons, nextCursor = page.NextCursor });
        });

        group.MapGet("/export/insights/{insightId:guid}", async (
            Guid projectId,
            Guid insightId,
            string? format,
            HttpContext http,
            PulseDbContext db,
            InsightRunnerService runner,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            if (!TryParseFormat(format, out var parsedFormat))
            {
                return FormatProblem();
            }

            var insight = await db.Insights
                .SingleOrDefaultAsync(i => i.ProjectId == projectId && i.Id == insightId, ct);

            if (insight is null)
            {
                return Results.NotFound();
            }

            var run = await runner.RunAsync(insight, ct);
            if (!run.Ok)
            {
                return Results.Problem(
                    title: "Insight failed to run",
                    detail: run.Error,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return parsedFormat == "csv"
                ? Results.Text(ExportService.QueryResultCsv(run.Result!).Content, "text/csv")
                : Results.Ok(run.Result);
        });

        // --- Async export jobs -------------------------------------------------

        group.MapPost("/exports", async (
            Guid projectId,
            CreateExportJobRequest request,
            HttpContext http,
            PulseDbContext db,
            ExportSignal signal,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var errors = new Dictionary<string, string[]>();
            var type = request.Type?.Trim().ToLowerInvariant();
            if (type is not ("events" or "persons" or "insight"))
            {
                errors["type"] = ["Type must be one of: events, persons, insight."];
            }

            if (!TryParseFormat(request.Format, out var format))
            {
                errors["format"] = ["Format must be 'csv' or 'json'."];
            }

            if (type == "insight" && request.InsightId is null)
            {
                errors["insightId"] = ["Insight exports need an 'insightId'."];
            }

            if (request.Filters is { } filters
                && (filters.ValueKind != JsonValueKind.Array
                    || !FilterParsing.TryParseJson(filters.GetRawText(), out _, out _)))
            {
                errors["filters"] = ["filters must be a valid filter array."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var parameters = new Dictionary<string, object?>();
            if (request.Event is not null)
            {
                parameters["event"] = request.Event.Trim();
            }

            if (request.From is { } fromValue)
            {
                parameters["from"] = fromValue.ToString("O");
            }

            if (request.To is { } toValue)
            {
                parameters["to"] = toValue.ToString("O");
            }

            if (request.Filters is { ValueKind: JsonValueKind.Array } filterArray)
            {
                parameters["filters"] = filterArray;
            }

            if (request.InsightId is { } insightId)
            {
                parameters["insightId"] = insightId.ToString();
            }

            var job = new ExportJob
            {
                ProjectId = projectId,
                Type = type!,
                Format = format,
                ParamsJson = JsonSerializer.Serialize(parameters),
            };

            db.ExportJobs.Add(job);
            await db.SaveChangesAsync(ct);
            signal.Ring();

            return Results.Accepted(
                $"/api/projects/{projectId}/exports/{job.Id}", ToResponse(job));
        });

        group.MapGet("/exports/{jobId:guid}", async (
            Guid projectId,
            Guid jobId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var job = await db.ExportJobs
                .SingleOrDefaultAsync(j => j.ProjectId == projectId && j.Id == jobId, ct);

            return job is null ? Results.NotFound() : Results.Ok(ToResponse(job));
        });

        group.MapGet("/exports/{jobId:guid}/download", async (
            Guid projectId,
            Guid jobId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var job = await db.ExportJobs
                .SingleOrDefaultAsync(j => j.ProjectId == projectId && j.Id == jobId, ct);

            if (job is null)
            {
                return Results.NotFound();
            }

            if (job.Status != ExportJobStatus.Completed)
            {
                return Results.Problem(
                    title: "Export not ready",
                    detail: $"Job status is '{job.Status.ToString().ToLowerInvariant()}'.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Text(job.ResultContent!, job.ContentType ?? "application/json");
        });

        return app;
    }

    private static bool TryParseFormat(string? raw, out string format)
    {
        format = raw?.Trim().ToLowerInvariant() ?? "json";
        return format is "csv" or "json";
    }

    private static IResult FormatProblem() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["format"] = ["Format must be 'csv' or 'json'."],
        });

    private static IResult CursorProblem() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["cursor"] = ["Invalid cursor."],
        });

    private static ExportJobResponse ToResponse(ExportJob job) =>
        new(
            job.Id,
            job.ProjectId,
            job.Type,
            job.Format,
            job.Status.ToString().ToLowerInvariant(),
            job.RowCount,
            job.Error,
            job.CreatedAt,
            job.CompletedAt);
}
