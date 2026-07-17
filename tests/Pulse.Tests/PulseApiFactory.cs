using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Infrastructure;

namespace Pulse.Tests;

/// <summary>
/// Boots the API against a private in-memory SQLite database. The connection
/// is held open for the factory's lifetime so the schema survives between
/// requests; each factory instance gets a fresh database.
/// </summary>
public class PulseApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection;

    public PulseApiFactory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var descriptor = services.Single(d =>
                d.ServiceType == typeof(DbContextOptions<PulseDbContext>));
            services.Remove(descriptor);

            services.AddDbContext<PulseDbContext>(options =>
                options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
