using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Pulse.Api.Auth;
using Pulse.Api.Endpoints;
using Pulse.Domain;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PulseDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Pulse")
                      ?? "Data Source=pulse.db"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IdentityService>();
builder.Services.AddScoped<CaptureService>();
builder.Services.AddScoped<QueryService>();
builder.Services.AddScoped<CohortService>();
builder.Services.AddScoped<FeatureFlagService>();
builder.Services.AddScoped<InsightRunnerService>();
builder.Services.AddSingleton<IngestionSignal>();
builder.Services.AddSingleton<IngestionCounters>();
builder.Services.AddScoped<IngestionProcessor>();
builder.Services.AddHostedService<Pulse.Api.Ingestion.IngestionWorker>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<ExportJobProcessor>();
builder.Services.AddSingleton<ExportSignal>();
builder.Services.AddHostedService<Pulse.Api.Export.ExportWorker>();
builder.Services.AddScoped<DemoDataSeeder>();
builder.Services.AddScoped<JwtTokenIssuer>();
builder.Services.AddScoped<ProjectAccessService>();

// Management-API auth: JWT sessions and pk_user_ personal keys share the
// Authorization header; a policy scheme dispatches on the token's prefix.
const string smartScheme = "BearerOrPersonalKey";
var jwtSecret = builder.Configuration["Jwt:Secret"]
                ?? throw new InvalidOperationException("Jwt:Secret is not configured.");

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = smartScheme;
        options.DefaultChallengeScheme = smartScheme;
    })
    .AddPolicyScheme(smartScheme, "JWT or personal API key", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var header = context.Request.Headers.Authorization.FirstOrDefault();
            return header?.StartsWith($"Bearer {ApiKeyGenerator.PersonalPrefix}", StringComparison.Ordinal) == true
                ? PersonalApiKeyAuthenticationHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? JwtTokenIssuer.DefaultIssuer,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? JwtTokenIssuer.DefaultIssuer,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        };
    })
    .AddScheme<AuthenticationSchemeOptions, PersonalApiKeyAuthenticationHandler>(
        PersonalApiKeyAuthenticationHandler.SchemeName, displayName: null, configureOptions: null);

builder.Services.AddAuthorization();

// Rate limit /capture per write key (falling back to client IP): fixed
// one-minute windows, no queueing — SDKs should back off and retry.
var capturePermitLimit = builder.Configuration.GetValue("RateLimiting:Capture:PermitLimit", 300);
var captureWindowSeconds = builder.Configuration.GetValue("RateLimiting:Capture:WindowSeconds", 60);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("capture", context =>
    {
        var key = context.Request.Headers["X-Api-Key"].FirstOrDefault()
                  ?? context.Connection.RemoteIpAddress?.ToString()
                  ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = capturePermitLimit,
            Window = TimeSpan.FromSeconds(captureWindowSeconds),
            QueueLimit = 0,
        });
    });
});

// RFC 7807 responses for every error path, including unhandled exceptions
// and malformed request bodies.
builder.Services.AddProblemDetails();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
    db.Database.EnsureCreated();
}

// `dotnet run --project src/Pulse.Api -- seed` generates demo data and exits.
if (args.Contains("seed", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
    var result = await seeder.SeedAsync();

    Console.WriteLine("Demo data generated:");
    Console.WriteLine($"  Project   : {result.ProjectName} ({result.ProjectId})");
    Console.WriteLine($"  Write key : {result.ApiKey}");
    Console.WriteLine($"  Read key  : {result.ReadKey}");
    Console.WriteLine($"  Login     : {result.DemoUserEmail} / {result.DemoUserPassword}");
    Console.WriteLine($"  Simulated : {result.Users} users, {result.Persons} persons, {result.Events} events");
    Console.WriteLine();
    Console.WriteLine("Try:");
    Console.WriteLine($"  GET /api/projects/{result.ProjectId}/insights/trend?event=pageview&interval=day");
    return;
}

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // Malformed request bodies (e.g. invalid JSON) surface as
    // BadHttpRequestException — report them as 400s, not 500s.
    StatusCodeSelector = ex => ex is BadHttpRequestException bad
        ? bad.StatusCode
        : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

app.UseMiddleware<Pulse.Api.RequestLoggingMiddleware>();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Health: liveness plus database and ingestion-queue probes. Database
// failure reports 503; a heavily backed-up queue degrades but stays 200.
app.MapGet("/health", async (PulseDbContext db, CancellationToken ct) =>
{
    const int queueDepthWarningThreshold = 10_000;

    int pending;
    int deadLetters;
    try
    {
        pending = await db.QueuedEvents.CountAsync(ct);
        deadLetters = await db.DeadLetterEvents.CountAsync(ct);
    }
    catch (Exception)
    {
        return Results.Json(new
        {
            status = "unhealthy",
            service = "pulse-net",
            timestamp = DateTime.UtcNow,
            checks = new { database = "failing", queue = (object?)null },
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var status = pending > queueDepthWarningThreshold ? "degraded" : "healthy";

    return Results.Ok(new
    {
        status,
        service = "pulse-net",
        timestamp = DateTime.UtcNow,
        checks = new
        {
            database = "ok",
            queue = new { pending, deadLetters },
        },
    });
});

app.MapAuthEndpoints();
app.MapProjectEndpoints();
app.MapCaptureEndpoints();
app.MapPersonEndpoints();
app.MapInsightEndpoints();
app.MapCohortEndpoints();
app.MapFeatureFlagEndpoints();
app.MapDashboardEndpoints();
app.MapIngestionEndpoints();
app.MapDataManagementEndpoints();
app.MapExportEndpoints();

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
