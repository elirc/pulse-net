using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

public class BoundedTrendTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;
    public BoundedTrendTests(PulseApiFactory factory) { _factory = factory; _client = factory.CreateClient(); }

    [Fact]
    public async Task WithinBudgetTrendIsCompleteZeroFilledAndCountsNullPerson()
    {
        var project = await CreateProjectAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.Events.AddRange(
                new AnalyticsEvent { ProjectId = project.Id, Name = "view", DistinctId = "unresolved", PersonId = null, Timestamp = DateTimeOffset.Parse("2026-05-01T10:00:00Z") },
                new AnalyticsEvent { ProjectId = project.Id, Name = "view", DistinctId = "unresolved-2", PersonId = null, Timestamp = DateTimeOffset.Parse("2026-05-01T10:00:00Z") });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/projects/{project.Id}/insights/trend-bounded?event=view&from=2026-05-01T10:00:00Z&to=2026-05-01T11:00:00Z&interval=hour");
        response.EnsureSuccessStatusCode();
        var json = (await response.Content.ReadFromJsonAsync<JsonElement>());
        var buckets = json.GetProperty("buckets").EnumerateArray().ToArray();
        Assert.Equal([2, 0], buckets.Select(b => b.GetProperty("count").GetInt32()));
        Assert.Equal(1, buckets[0].GetProperty("uniquePersons").GetInt32());
    }

    [Fact]
    public async Task MissingExplicitRangeAndPersonFilterAreRejected()
    {
        var project = await CreateProjectAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/projects/{project.Id}/insights/trend-bounded?event=view&interval=day")).StatusCode);
        var filter = Uri.EscapeDataString("[{\"type\":\"person\",\"property\":\"plan\",\"operator\":\"equals\",\"value\":\"pro\"}]");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/projects/{project.Id}/insights/trend-bounded?event=view&from=2026-05-01T00:00:00Z&to=2026-05-02T00:00:00Z&interval=day&filters={filter}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/projects/{project.Id}/insights/trend-bounded?event=view&from=2026-05-01T00:00:00Z&to=2026-05-02T00:00:00Z&interval=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/projects/{project.Id}/insights/trend-bounded?event={new string('x', 201)}&from=2026-05-01T00:00:00Z&to=2026-05-02T00:00:00Z&interval=day")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/projects/{project.Id}/insights/trend-bounded?event=view&from=2026-01-01T00:00:00Z&to=2026-04-02T00:00:00Z&interval=hour")).StatusCode);
    }

    [Fact]
    public async Task HourDayAndWeekBucketsExactlyMatchLegacyAndReadKeyIsAccepted()
    {
        var project = await CreateProjectAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var known = new Person { ProjectId = project.Id };
            db.Persons.Add(known);
            db.Events.AddRange(
                new AnalyticsEvent { ProjectId = project.Id, Name = "same", DistinctId = "null-a", PersonId = null, Timestamp = DateTimeOffset.Parse("2026-05-04T10:00:00Z") },
                new AnalyticsEvent { ProjectId = project.Id, Name = "same", DistinctId = "null-b", PersonId = null, Timestamp = DateTimeOffset.Parse("2026-05-04T10:00:00Z") },
                new AnalyticsEvent { ProjectId = project.Id, Name = "same", DistinctId = "known", PersonId = known.Id, Timestamp = DateTimeOffset.Parse("2026-05-05T12:00:00Z") });
            await db.SaveChangesAsync();
        }

        foreach (var interval in new[] { "hour", "day", "week" })
        {
            var query = $"event=same&from=2026-05-04T00:00:00Z&to=2026-05-06T00:00:00Z&interval={interval}";
            var legacy = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{project.Id}/insights/trend?{query}");
            var bounded = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{project.Id}/insights/trend-bounded?{query}");
            Assert.Equal(Project(legacy.GetProperty("buckets")), Project(bounded.GetProperty("buckets")));
        }

        using var readClient = _factory.CreateClient();
        readClient.DefaultRequestHeaders.Add("X-Api-Key", project.ReadKey);
        var response = await readClient.GetAsync($"/api/projects/{project.Id}/insights/trend-bounded?event=same&from=2026-05-04T00:00:00Z&to=2026-05-06T00:00:00Z&interval=day");
        response.EnsureSuccessStatusCode();

        static string[] Project(JsonElement buckets) => buckets.EnumerateArray()
            .Select(b => $"{b.GetProperty("start").GetDateTimeOffset():O}|{b.GetProperty("count").GetInt32()}|{b.GetProperty("uniquePersons").GetInt32()}")
            .ToArray();
    }

    private async Task<ProjectResponse> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name = $"Bounded {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
    }
}
