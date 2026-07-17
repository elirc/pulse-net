using System.Text.Json;
using Pulse.Api.Contracts;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static class CaptureEndpoints
{
    public const int MaxBatchSize = 1000;

    public static IEndpointRouteBuilder MapCaptureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/capture", async (
            CaptureRequest request,
            HttpContext http,
            CaptureService capture,
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

            var (events, errors) = Unwrap(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var result = await capture.IngestAsync(project, events, ct);
            return Results.Ok(new CaptureResponse("ok", result.Ingested));
        });

        return app;
    }

    private static (List<IncomingEvent> Events, Dictionary<string, string[]> Errors) Unwrap(
        CaptureRequest request)
    {
        var events = new List<IncomingEvent>();
        var errors = new Dictionary<string, string[]>();

        if (request.Batch is not null)
        {
            if (request.Batch.Count == 0)
            {
                errors["batch"] = ["Batch must contain at least one event."];
                return (events, errors);
            }

            if (request.Batch.Count > MaxBatchSize)
            {
                errors["batch"] = [$"Batch exceeds the maximum of {MaxBatchSize} events."];
                return (events, errors);
            }

            for (var i = 0; i < request.Batch.Count; i++)
            {
                var item = request.Batch[i];
                var itemErrors = Validate(item.Event, item.DistinctId, $"batch[{i}].");
                foreach (var (key, value) in itemErrors)
                {
                    errors[key] = value;
                }

                if (itemErrors.Count == 0)
                {
                    events.Add(ToIncoming(item.Event!, item.DistinctId!, item.Timestamp, item.Properties));
                }
            }

            return (events, errors);
        }

        var singleErrors = Validate(request.Event, request.DistinctId, prefix: string.Empty);
        foreach (var (key, value) in singleErrors)
        {
            errors[key] = value;
        }

        if (singleErrors.Count == 0)
        {
            events.Add(ToIncoming(request.Event!, request.DistinctId!, request.Timestamp, request.Properties));
        }

        return (events, errors);
    }

    private static Dictionary<string, string[]> Validate(string? eventName, string? distinctId, string prefix)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(eventName))
        {
            errors[$"{prefix}event"] = ["Event name is required."];
        }

        if (string.IsNullOrWhiteSpace(distinctId))
        {
            errors[$"{prefix}distinct_id"] = ["distinct_id is required."];
        }

        return errors;
    }

    private static IncomingEvent ToIncoming(
        string eventName,
        string distinctId,
        DateTimeOffset? timestamp,
        JsonElement? properties)
    {
        var propertiesJson =
            properties is { ValueKind: JsonValueKind.Object } element
                ? element.GetRawText()
                : "{}";

        return new IncomingEvent(eventName.Trim(), distinctId.Trim(), timestamp, propertiesJson);
    }
}
