using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Infrastructure;

namespace Pulse.Tests;

/// <summary>
/// Boots the API against a private shared-cache in-memory SQLite database.
/// Every DbContext opens its own connection to the same named database, so
/// the background ingestion worker and request handlers can operate
/// concurrently (a single SqliteConnection is not thread-safe). The
/// keep-alive connection pins the database for the factory's lifetime;
/// each factory instance gets a fresh database.
/// </summary>
public class PulseApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly SqliteConnection _keepAlive;

    public PulseApiFactory()
    {
        _connectionString = $"Data Source=pulse-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var descriptor = services.Single(d =>
                d.ServiceType == typeof(DbContextOptions<PulseDbContext>));
            services.Remove(descriptor);

            services.AddDbContext<PulseDbContext>(options =>
                options.UseSqlite(_connectionString));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAlive.Dispose();
        }
    }
}
