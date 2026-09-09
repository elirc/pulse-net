using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pulse.Infrastructure;

namespace Pulse.Api.Database;

/// <summary>Schema tooling does not start the web host, workers, or touch a practice database.</summary>
public sealed class DesignTimePulseFactory : IDesignTimeDbContextFactory<PulseDbContext>
{
    public PulseDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<PulseDbContext>().UseSqlite("Data Source=:memory:").Options);
}
