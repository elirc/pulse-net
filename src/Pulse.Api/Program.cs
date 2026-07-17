using Microsoft.EntityFrameworkCore;
using Pulse.Api.Endpoints;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PulseDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Pulse")
                      ?? "Data Source=pulse.db"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CaptureService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "pulse-net",
    timestamp = DateTime.UtcNow,
}));

app.MapProjectEndpoints();
app.MapCaptureEndpoints();

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
