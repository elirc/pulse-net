using Microsoft.Extensions.DependencyInjection;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class DemoDataSeederTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;

    public DemoDataSeederTests(PulseApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Seed_ProducesACoherentDemoDataset()
    {
        using var scope = _factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();

        var result = await seeder.SeedAsync(users: 15, days: 14, seed: 42);

        // The project exists and owns everything generated.
        var project = db.Projects.Single(p => p.Id == result.ProjectId);
        Assert.StartsWith("pk_live_", project.ApiKey);
        Assert.True(result.Events > 0, "Seeder should generate events.");
        Assert.Equal(result.Events, db.Events.Count(e => e.ProjectId == project.Id));
        Assert.True(result.Persons > 0, "Seeder should create persons.");

        // The funnel is coherent: signup >= activate >= purchase.
        int Count(string name) => db.Events.Count(e => e.ProjectId == project.Id && e.Name == name);
        Assert.True(Count("signup") >= Count("activate"));
        Assert.True(Count("activate") >= Count("purchase"));
        Assert.True(Count("pageview") > 0);

        // Identify events merged anon devices into identified users.
        Assert.True(Count("$identify") > 0);
        var identifiedPersons = db.Persons
            .Where(p => p.ProjectId == project.Id)
            .AsEnumerable()
            .Count(p => p.PropertiesJson.Contains("\"email\""));
        Assert.Equal(Count("signup"), identifiedPersons);

        // Every event resolved to a person.
        Assert.Equal(0, db.Events.Count(e => e.ProjectId == project.Id && e.PersonId == null));
    }

    [Fact]
    public async Task Seed_IsDeterministicForTheSameSeed()
    {
        using var scope = _factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();

        var first = await seeder.SeedAsync(users: 10, days: 7, seed: 7);
        var second = await seeder.SeedAsync(users: 10, days: 7, seed: 7);

        Assert.NotEqual(first.ProjectId, second.ProjectId); // Separate projects...
        Assert.Equal(first.Events, second.Events);          // ...same simulated traffic.
        Assert.Equal(first.Persons, second.Persons);
    }
}
