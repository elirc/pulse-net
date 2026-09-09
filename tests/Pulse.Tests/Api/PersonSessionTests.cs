using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

public class PersonSessionTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;
    public PersonSessionTests(PulseApiFactory factory) { _factory = factory; _client = factory.CreateClient(); }

    [Fact]
    public async Task HalfOpenWindowAndEqualGapProduceWindowLocalSessions()
    {
        var project = await CreateProjectAsync();
        var person = new Person { ProjectId = project.Id };
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.Persons.Add(person);
            db.Events.AddRange(Event(0), Event(10), Event(40), Event(60));
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/projects/{project.Id}/persons/{person.Id}/sessions?from=2026-05-01T10:00:00Z&to=2026-05-01T11:00:00Z&gapMinutes=30");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sessions = json.GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(2, sessions.Length);
        Assert.Equal([2, 1], sessions.Select(s => s.GetProperty("eventCount").GetInt32()));
        Assert.True(sessions[0].GetProperty("mayStartBeforeWindow").GetBoolean());
        Assert.True(sessions[1].GetProperty("mayContinueAfterWindow").GetBoolean());

        AnalyticsEvent Event(int minute) => new() { ProjectId = project.Id, PersonId = person.Id,
            Name = "view", DistinctId = "canonical", Timestamp = DateTimeOffset.Parse("2026-05-01T10:00:00Z").AddMinutes(minute) };
    }

    [Fact]
    public async Task ExistingEmptyPersonReturnsEmptyButForeignPersonIsNotFound()
    {
        var project = await CreateProjectAsync();
        var person = new Person { ProjectId = project.Id };
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.Persons.Add(person);
            await db.SaveChangesAsync();
        }
        var suffix = "?from=2026-05-01T10:00:00Z&to=2026-05-02T10:00:00Z&gapMinutes=30";
        var empty = await _client.GetAsync($"/api/projects/{project.Id}/persons/{person.Id}/sessions{suffix}");
        empty.EnsureSuccessStatusCode();
        Assert.Empty((await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessions").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/projects/{project.Id}/persons/{Guid.NewGuid()}/sessions{suffix}")).StatusCode);
    }

    [Fact]
    public async Task IdentityServiceMergeChangesCurrentCanonicalSessionHistory()
    {
        var project = await CreateProjectAsync();
        Guid canonicalId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var identities = new IdentityService(db);
            var identified = await identities.ResolveAsync(project.Id, "account");
            var anonymous = await identities.ResolveAsync(project.Id, "device");
            await db.SaveChangesAsync();
            db.Events.AddRange(
                new AnalyticsEvent { ProjectId = project.Id, PersonId = identified.Id, Name = "view", DistinctId = "account", Timestamp = DateTimeOffset.Parse("2026-05-01T10:00:00Z") },
                new AnalyticsEvent { ProjectId = project.Id, PersonId = anonymous.Id, Name = "view", DistinctId = "device", Timestamp = DateTimeOffset.Parse("2026-05-01T10:10:00Z") });
            await db.SaveChangesAsync();
            canonicalId = (await identities.IdentifyAsync(project.Id, "account", "device")).Id;
        }

        var json = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{project.Id}/persons/{canonicalId}/sessions?from=2026-05-01T10:00:00Z&to=2026-05-01T11:00:00Z&gapMinutes=30");
        Assert.Equal(2, json.GetProperty("sessions")[0].GetProperty("eventCount").GetInt32());
    }

    [Fact]
    public async Task OffsetEquivalentWindowAndTimestampTiesUseSqlIdOrder()
    {
        var project = await CreateProjectAsync();
        var person = new Person { ProjectId = project.Id };
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.Persons.Add(person);
            db.Events.AddRange(
                new AnalyticsEvent { Id = high, ProjectId = project.Id, PersonId = person.Id, Name = "v", DistinctId = "d", Timestamp = DateTimeOffset.Parse("2026-05-01T10:00:00Z") },
                new AnalyticsEvent { Id = low, ProjectId = project.Id, PersonId = person.Id, Name = "v", DistinctId = "d", Timestamp = DateTimeOffset.Parse("2026-05-01T10:00:00Z") });
            await db.SaveChangesAsync();
        }
        var json = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{project.Id}/persons/{person.Id}/sessions?from=2026-05-01T12:00:00%2B02:00&to=2026-05-01T13:00:00%2B02:00&gapMinutes=30");
        var session = json.GetProperty("sessions")[0];
        Assert.Equal(low, session.GetProperty("firstEventId").GetGuid());
        Assert.Equal(high, session.GetProperty("lastEventId").GetGuid());

        using var readClient = _factory.CreateClient();
        readClient.DefaultRequestHeaders.Add("X-Api-Key", project.ReadKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await readClient.GetAsync($"/api/projects/{project.Id}/persons/{person.Id}/sessions?from=2026-05-01T10:00:00Z&to=2026-05-01T11:00:00Z")).StatusCode);
    }

    private async Task<ProjectResponse> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name = $"Sessions {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
    }
}
