using Microsoft.EntityFrameworkCore;
using Pulse.Api.Endpoints;
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
builder.Services.AddScoped<DemoDataSeeder>();

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
    Console.WriteLine($"  API key   : {result.ApiKey}");
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

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "pulse-net",
    timestamp = DateTime.UtcNow,
}));

app.MapProjectEndpoints();
app.MapCaptureEndpoints();
app.MapPersonEndpoints();
app.MapInsightEndpoints();

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
