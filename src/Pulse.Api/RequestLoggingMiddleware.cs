using System.Diagnostics;
using Pulse.Infrastructure.Services;

namespace Pulse.Api;

/// <summary>
/// One structured log line per request: method, path, status and elapsed
/// milliseconds. Health probes are skipped to keep the noise down.
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        using var captureTrace = context.Request.Path == "/capture" ? IngestionTrace.Producer("capture") : null;
        if (captureTrace is not null) context.Response.Headers["X-Trace-Id"] = captureTrace.TraceId.ToString();
        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogInformation(
                "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms trace {TraceId} span {SpanId}",
                context.Request.Method,
                (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)",
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                Activity.Current?.TraceId.ToString() ?? context.Response.Headers["X-Trace-Id"].FirstOrDefault(),
                Activity.Current?.SpanId.ToString());
        }
    }
}
