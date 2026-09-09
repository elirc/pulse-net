using System.Text.Json;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;
using Pulse.Api.Auth;

namespace Pulse.Api.Endpoints;

public static class CaptureEndpoints
{
    public const int MaxBatchSize = CaptureRequestParser.MaxBatchSize;

    public static IEndpointRouteBuilder MapCaptureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/projects/{projectId:guid}/capture/validate", async (Guid projectId, HttpContext http,
            ProjectAccessService access, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var (body, bodyError) = await LimitedJsonBody.ReadAsync(http.Request, 1024 * 1024, ct);
            if (bodyError is not null) return bodyError;
            if (body is not { ValueKind: JsonValueKind.Object } value) return InputRules.Problem("body", "Body must be a capture object.");
            if (value.EnumerateObject().Any(p => p.Name.Equals("api_key", StringComparison.OrdinalIgnoreCase)))
                return InputRules.Problem("api_key", "Do not supply a credential; the authorized route selects the project.");
            CaptureRequest? request;
            try { request = value.Deserialize<CaptureRequest>(JsonSerializerOptions.Web); }
            catch (JsonException) { return InputRules.Problem("body", "Capture fields have invalid JSON types."); }
            var (events, errors) = CaptureRequestParser.Parse(request!);
            if (events.Any(e => e.ClientEventId is not null) && !CaptureFingerprint.HasUniqueObjectMembers(value))
                errors["body"] = ["ID-bearing payloads must not contain duplicate object member names."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { valid = true, eventCount = events.Count, preview = events.Take(3).Select(e => new
                { @event = e.Name, distinctId = e.DistinctId, timestamp = e.Timestamp, eventId = e.ClientEventId, properties = JsonSerializer.Deserialize<JsonElement>(e.PropertiesJson) }),
                previewTruncated = events.Count > 3 });
        });

        app.MapPost("/capture", async (
            HttpContext http,
            CaptureService capture,
            QueueAdmissionService admission,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var (body, bodyError) = await LimitedJsonBody.ReadAsync(http.Request, 30 * 1024 * 1024, ct);
            if (bodyError is not null) return bodyError;
            if (body is not { ValueKind: JsonValueKind.Object } value) return InputRules.Problem("body", "Body must be a capture object.");
            CaptureRequest? request;
            try { request = value.Deserialize<CaptureRequest>(JsonSerializerOptions.Web); }
            catch (JsonException) { return InputRules.Problem("body", "Capture fields have invalid JSON types; event_id must be a UUID."); }
            if (request is null) return InputRules.Problem("body", "Body must be a capture object.");
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

            var (events, errors) = CaptureRequestParser.Parse(request);
            var hasIds = events.Any(e => e.ClientEventId is not null);
            if (hasIds && !CaptureFingerprint.HasUniqueObjectMembers(value)) errors["body"] = ["ID-bearing payloads must not contain duplicate object member names."];
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var receiptHeader = http.Request.Headers["X-Capture-Receipt"];
            if (receiptHeader.Count > 1 || (receiptHeader.Count == 1 && !bool.TryParse(receiptHeader[0], out _)))
                return InputRules.Problem("X-Capture-Receipt", "Use true or false.");
            var wantsReceipt = receiptHeader.Count == 1 && bool.Parse(receiptHeader[0]!);
            if ((hasIds && !configuration.GetValue("Capture:IdempotencyEnabled", true)) || (wantsReceipt && !configuration.GetValue("Capture:ReceiptsEnabled", true)))
                return Results.Problem("The requested admission capability is temporarily unavailable.", statusCode: 503);
            var result = await admission.AdmitAsync(project.Id, events, wantsReceipt, ct);
            if (result.Status != 202) return AdmissionFailure(http, result.Status, result.Code!);
            if (!hasIds && !wantsReceipt) return Results.Accepted(value: new CaptureResponse("queued", result.Queued));
            return Results.Accepted(value: new { status = "queued", result.Queued, result.Deduplicated, result.ReceiptId,
                statusUrl = result.ReceiptId is { } receiptId ? $"/api/projects/{project.Id}/capture-receipts/{receiptId}" : null });
        }).RequireRateLimiting("capture");

        return app;
    }

    internal static IResult AdmissionFailure(HttpContext http, int status, string code)
    {
        if (status == 429) http.Response.Headers.RetryAfter = "1";
        return Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code });
    }

}
