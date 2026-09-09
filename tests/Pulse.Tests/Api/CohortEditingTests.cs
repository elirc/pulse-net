using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

public class CohortEditingTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private static object[] Rules(string plan) => [new { kind = "property", property = "plan", @operator = "equals", value = plan }];
    private async Task<(HttpClient Client, ProjectResponse Project, Person[] People)> SetupAsync()
    {
        var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        var project = await PostAsync<ProjectResponse>(client, "/api/projects", new { name = "Audiences" });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var people = new[] { "pro", "pro", "basic", "basic" }.Select(plan => new Person
        { ProjectId = project.Id, PropertiesJson = JsonSerializer.Serialize(new { plan }) }).ToArray();
        db.Persons.AddRange(people);
        await db.SaveChangesAsync();
        return (client, project, people);
    }
    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Preview_CountsBeforeSampling_AndMatchesStoredAndRulesWithoutWrites()
    {
        var (client, project, people) = await SetupAsync();
        var root = $"/api/projects/{project.Id}/cohorts";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Events.Add(new AnalyticsEvent { ProjectId = project.Id, PersonId = people[0].Id, Name = "purchase", DistinctId = "p", Timestamp = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        var preview = await PostAsync<CohortPreview>(client, root + "/preview", new { rules = Rules("pro"), sampleLimit = 1 });
        Assert.Equal(2, preview.Count);
        Assert.Equal(new[] { people.Take(2).Select(p => p.Id).Min() }, preview.SamplePersonIds);
        Assert.Equal(0, await db.Cohorts.CountAsync(c => c.ProjectId == project.Id));
        Assert.Equal(0, await db.CohortPersons.CountAsync(cp => people.Select(p => p.Id).Contains(cp.PersonId)));
        var combined = Rules("pro").Concat(new object[] { new { kind = "performed_event", @event = "purchase", days = 30, minCount = 1 } }).ToArray();
        var andPreview = await PostAsync<CohortPreview>(client, root + "/preview", new { rules = combined });
        Assert.Equal(new[] { people[0].Id }, andPreview.SamplePersonIds);
        var saved = await PostAsync<CohortResponse>(client, root, new { name = "Same", type = "dynamic", rules = combined });
        var members = (await client.GetFromJsonAsync<CohortMembersResponse>(root + $"/{saved.Id}/persons"))!;
        Assert.Equal(andPreview.SamplePersonIds, members.PersonIds);
        var empty = await PostAsync<CohortPreview>(client, root + "/preview", new { rules = new[] { new { kind = "performed_event", @event = "unknown" } } });
        Assert.Equal(0, empty.Count);
    }

    [Fact]
    public async Task RuleReplacement_RetainsIdentity_ChangesFlagAudience_AndRejectsInvalidCandidate()
    {
        var (client, project, people) = await SetupAsync();
        var root = $"/api/projects/{project.Id}/cohorts";
        var cohort = await PostAsync<CohortResponse>(client, root, new { name = "Audience", type = "dynamic", rules = Rules("basic") });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.PersonDistinctIds.AddRange(people.Select((p, index) => new PersonDistinctId { ProjectId = project.Id, PersonId = p.Id, DistinctId = "p" + index }));
        db.FeatureFlags.Add(new FeatureFlag { ProjectId = project.Id, Key = "audience", Active = true, RolloutPercentage = 100,
            FiltersJson = JsonSerializer.Serialize(new[] { new { type = "cohort", value = cohort.Id.ToString() } }) });
        await db.SaveChangesAsync();
        async Task<bool> Decide(int index)
        {
            var json = await PostAsync<JsonElement>(client, "/decide", new { api_key = project.ApiKey, distinct_id = "p" + index });
            return json.GetProperty("featureFlags").GetProperty("audience").GetBoolean();
        }
        Assert.False(await Decide(0));
        Assert.True(await Decide(2));
        var update = await client.PutAsJsonAsync(root + $"/{cohort.Id}/rules", new { rules = Rules("pro") });
        update.EnsureSuccessStatusCode();
        var result = (await update.Content.ReadFromJsonAsync<CohortResponse>())!;
        Assert.Equal(cohort.Id, result.Id);
        Assert.Equal(cohort.Name, result.Name);
        Assert.Equal(cohort.CreatedAt, result.CreatedAt);
        Assert.True(await Decide(0));
        Assert.False(await Decide(2));
        var invalid = Rules("basic").Concat(new object[] { new { kind = "invalid" } });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(root + $"/{cohort.Id}/rules", new { rules = invalid })).StatusCode);
        Assert.True(await Decide(0));
        Assert.Equal(0, await db.CohortPersons.CountAsync(cp => cp.CohortId == cohort.Id));
    }

    [Fact]
    public async Task Snapshot_FreezesMembershipOnly_AndLeavesDynamicSourceIntact()
    {
        var (client, project, people) = await SetupAsync();
        var root = $"/api/projects/{project.Id}/cohorts";
        var source = await PostAsync<CohortResponse>(client, root, new { name = "Live", type = "dynamic", rules = Rules("pro") });
        var response = await client.PostAsJsonAsync(root + $"/{source.Id}/snapshot", new { name = " Launch " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var snapshot = await response.Content.ReadFromJsonAsync<JsonElement>();
        var copy = snapshot.GetProperty("cohort").Deserialize<CohortResponse>(JsonSerializerOptions.Web)!;
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Launch", copy.Name);
        Assert.Equal("static", copy.Type);
        Assert.Equal(2, snapshot.GetProperty("memberCount").GetInt32());
        Assert.Contains(copy.Id.ToString(), response.Headers.Location!.ToString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        await db.Persons.Where(p => p.Id == people[0].Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.PropertiesJson, "{\"plan\":\"basic\"}"));
        var live = (await client.GetFromJsonAsync<CohortMembersResponse>(root + $"/{source.Id}/persons"))!;
        var frozen = (await client.GetFromJsonAsync<CohortMembersResponse>(root + $"/{copy.Id}/persons"))!;
        Assert.Equal(1, live.Count);
        Assert.Equal(2, frozen.Count);
        var unchanged = (await client.GetFromJsonAsync<CohortResponse>(root + $"/{source.Id}"))!;
        Assert.Equal("dynamic", unchanged.Type);
        Assert.True(JsonElement.DeepEquals(source.Rules, unchanged.Rules));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + $"/{copy.Id}/snapshot", new { name = "No" })).StatusCode);
    }

    [Fact]
    public async Task StaticReplacement_ComputesSetDifference_IsIdempotent_AndValidatesBeforeDeletion()
    {
        var (client, project, people) = await SetupAsync();
        var root = $"/api/projects/{project.Id}/cohorts";
        var cohort = await PostAsync<CohortResponse>(client, root, new { name = "Static", type = "static", personIds = people.Take(3).Select(p => p.Id) });
        var desired = new[] { people[1].Id, people[2].Id, people[3].Id, people[3].Id };
        async Task<JsonElement> Replace(Guid[] ids)
        {
            var response = await client.PutAsJsonAsync(root + $"/{cohort.Id}/persons", new { personIds = ids });
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        var changed = await Replace(desired);
        Assert.Equal(1, changed.GetProperty("added").GetInt32());
        Assert.Equal(1, changed.GetProperty("removed").GetInt32());
        Assert.Equal(2, changed.GetProperty("unchanged").GetInt32());
        Assert.Equal(3, changed.GetProperty("total").GetInt32());
        var repeated = await Replace(desired);
        Assert.Equal(0, repeated.GetProperty("added").GetInt32());
        Assert.Equal(0, repeated.GetProperty("removed").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(root + $"/{cohort.Id}/persons", new { personIds = new[] { Guid.NewGuid() } })).StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<CohortMembersResponse>(root + $"/{cohort.Id}/persons"))!.Count);
        Assert.Equal(3, (await Replace([])).GetProperty("removed").GetInt32());
        Assert.Equal(0, (await client.GetFromJsonAsync<CohortMembersResponse>(root + $"/{cohort.Id}/persons"))!.Count);
    }

    [Theory]
    [InlineData("{\"rules\":[]}")]
    [InlineData("{\"rules\":[{\"kind\":\"property\",\"property\":\"plan\",\"operator\":\"wrong\"}]}")]
    [InlineData("{\"rules\":[{\"kind\":\"performed_event\",\"event\":\"a\",\"days\":\"bad\"}]}")]
    [InlineData("{\"rules\":[{\"kind\":\"performed_event\",\"event\":\"a\"}],\"sampleLimit\":0}")]
    public async Task Preview_InvalidRequestsDoNotCreateRows(string json)
    {
        var (client, project, _) = await SetupAsync();
        var response = await client.PostAsJsonAsync($"/api/projects/{project.Id}/cohorts/preview", JsonSerializer.Deserialize<JsonElement>(json));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<PulseDbContext>().Cohorts.CountAsync(c => c.ProjectId == project.Id));
    }

    [Fact]
    public async Task NewRoutes_EnforceAuthentication_ProjectScope_AndRawInputCaps()
    {
        var (client, project, people) = await SetupAsync();
        var root = $"/api/projects/{project.Id}/cohorts";
        var cohort = await PostAsync<CohortResponse>(client, root, new { name = "Target", type = "static" });
        using var outsider = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await outsider.PostAsJsonAsync(root + "/preview", new { rules = Rules("pro") })).StatusCode);
        await TestAuth.AuthenticateAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync(root + "/preview", new { rules = Rules("pro") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync(root + $"/{cohort.Id}/persons", new { personIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync(root + $"/{cohort.Id}/rules", new { rules = Rules("pro") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync(root + $"/{cohort.Id}/snapshot", new { name = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(root + $"/{cohort.Id}/rules", new { rules = Rules("pro") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(root + $"/{cohort.Id}/persons", new { personIds = Enumerable.Repeat(people[0].Id, 1001) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root + "/preview", new { rules = Rules(new string('x', 33000)) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root + "/preview", new { rules = Enumerable.Repeat(Rules("pro")[0], 11) })).StatusCode);
        var other = await PostAsync<ProjectResponse>(client, "/api/projects", new { name = "Other" });
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/projects/{other.Id}/cohorts/{cohort.Id}/snapshot", new { name = "No" })).StatusCode);
    }
}
