using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

public class FlagExperienceTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private async Task<(HttpClient Client, ProjectResponse Project)> SetupAsync()
    {
        var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Flag experience" });
        response.EnsureSuccessStatusCode();
        return (client, (await response.Content.ReadFromJsonAsync<ProjectResponse>())!);
    }

    [Fact]
    public async Task Explanations_MatchDecideAcrossEveryGateAndLegacyFallback_WithoutPrivateData()
    {
        var (client, project) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        const string secret = "person-private-value-never-in-explanation";
        var person = new Person { ProjectId = project.Id, PropertiesJson = JsonSerializer.Serialize(new { plan = secret }) };
        db.Persons.Add(person);
        db.PersonDistinctIds.Add(new PersonDistinctId { ProjectId = project.Id, PersonId = person.Id, DistinctId = "known" });
        var flags = new[]
        {
            new FeatureFlag { ProjectId = project.Id, Key = "inactive", Active = false },
            new FeatureFlag { ProjectId = project.Id, Key = "miss", FiltersJson = "[{\"type\":\"person\",\"property\":\"plan\",\"operator\":\"equals\",\"value\":\"different\"}]" },
            new FeatureFlag { ProjectId = project.Id, Key = "zero", RolloutPercentage = 0 },
            new FeatureFlag { ProjectId = project.Id, Key = "on", RolloutPercentage = 100 },
            new FeatureFlag { ProjectId = project.Id, Key = "variant", Type = FeatureFlagType.Multivariate, VariantsJson = "[{\"key\":\"a\",\"rolloutPercentage\":40},{\"key\":\"b\",\"rolloutPercentage\":60}]" },
            new FeatureFlag { ProjectId = project.Id, Key = "bad-target", FiltersJson = "not-json" },
            new FeatureFlag { ProjectId = project.Id, Key = "bad-variant", Type = FeatureFlagType.Multivariate, VariantsJson = "not-json" },
        };
        db.FeatureFlags.AddRange(flags);
        await db.SaveChangesAsync();
        foreach (var identity in new[] { "known", "unknown" })
        {
            var decide = await (await client.PostAsJsonAsync("/decide", new { api_key = project.ApiKey, distinct_id = identity })).Content.ReadFromJsonAsync<JsonElement>();
            foreach (var flag in flags)
            {
                var response = await client.PostAsJsonAsync($"/api/projects/{project.Id}/feature-flags/{flag.Key}/explain", new { distinctId = identity });
                response.EnsureSuccessStatusCode();
                var raw = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain(secret, raw);
                Assert.DoesNotContain("properties", raw);
                var explanation = JsonSerializer.Deserialize<JsonElement>(raw);
                Assert.True(JsonElement.DeepEquals(decide.GetProperty("featureFlags").GetProperty(flag.Key), explanation.GetProperty("value")));
                Assert.Equal(identity == "known", explanation.GetProperty("personResolved").GetBoolean());
                Assert.Equal(4, explanation.GetProperty("stages").GetArrayLength());
                if (flag.Key == "inactive") Assert.All(explanation.GetProperty("stages").EnumerateArray().Skip(1), stage => Assert.Equal("skipped", stage.GetProperty("outcome").GetString()));
                if (flag.Key.StartsWith("bad-")) Assert.Single(explanation.GetProperty("warnings").EnumerateArray());
            }
        }
        Assert.Equal(1, await db.Persons.CountAsync(p => p.ProjectId == project.Id));
        Assert.Equal(flags.Length, await db.FeatureFlags.CountAsync(f => f.ProjectId == project.Id));
    }

    [Fact]
    public async Task Batch_PreservesIdentityOrder_MatchesSingleDecisions_AndRejectsPartialInvalidInput()
    {
        var (client, project) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.FeatureFlags.AddRange(new FeatureFlag { ProjectId = project.Id, Key = "A", RolloutPercentage = 43 },
            new FeatureFlag { ProjectId = project.Id, Key = "B", Active = false });
        await db.SaveChangesAsync();
        var path = $"/api/projects/{project.Id}/feature-flags/evaluate-batch";
        var batchResponse = await client.PostAsJsonAsync(path, new { distinctIds = new[] { " bob ", "alice", "unknown" }, keys = new[] { "B", "A" } });
        batchResponse.EnsureSuccessStatusCode();
        var batch = await batchResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "bob", "alice", "unknown" }, batch.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("distinctId").GetString()));
        foreach (var row in batch.GetProperty("results").EnumerateArray())
        {
            var one = await (await client.PostAsJsonAsync("/decide", new { api_key = project.ApiKey, distinct_id = row.GetProperty("distinctId").GetString() })).Content.ReadFromJsonAsync<JsonElement>();
            foreach (var key in new[] { "A", "B" }) Assert.True(JsonElement.DeepEquals(one.GetProperty("featureFlags").GetProperty(key), row.GetProperty("featureFlags").GetProperty(key)));
        }
        foreach (var input in new object[] {
            new { distinctIds = new[] { "a", " a " }, keys = new[] { "A" } },
            new { distinctIds = new[] { "a" }, keys = new[] { "A", "A" } },
            new { distinctIds = new[] { "a" }, keys = new[] { "A", "foreign" } },
            new { distinctIds = Enumerable.Range(0, 51).Select(i => "p" + i), keys = new[] { "A" } },
            new { distinctIds = new[] { "a" }, keys = Enumerable.Range(0, 21).Select(i => "k" + i) },
        }) Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, input)).StatusCode);
        Assert.Equal(0, await db.Persons.CountAsync(p => p.ProjectId == project.Id));
    }

    [Fact]
    public async Task Clone_IsInactiveWithNewIdentity_AndConflictsOnDuplicateOrInvalidStoredSource()
    {
        var (client, project) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var source = new FeatureFlag { ProjectId = project.Id, Key = "live", Name = "Live", Type = FeatureFlagType.Multivariate,
            RolloutPercentage = 75, VariantsJson = "[{\"key\":\"control\",\"rolloutPercentage\":100}]", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        db.FeatureFlags.Add(source);
        await db.SaveChangesAsync();
        var path = $"/api/projects/{project.Id}/feature-flags/live/clone";
        var response = await client.PostAsJsonAsync(path, new { key = "draft", name = " Draft ", active = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var clone = (await response.Content.ReadFromJsonAsync<FeatureFlagResponse>())!;
        Assert.False(clone.Active);
        Assert.Equal("Draft", clone.Name);
        Assert.NotEqual(source.Id, clone.Id);
        Assert.True(clone.CreatedAt > source.CreatedAt);
        Assert.Equal(source.RolloutPercentage, clone.RolloutPercentage);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(source.VariantsJson), clone.Variants));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { key = "draft", name = "Again" })).StatusCode);
        source.FiltersJson = "broken";
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { key = "invalid-copy", name = "No" })).StatusCode);
        Assert.Equal(2, await db.FeatureFlags.CountAsync(f => f.ProjectId == project.Id));
    }

    [Fact]
    public async Task DiagnosticsAndCloning_KeepManagementAuthenticationAndProjectScoping()
    {
        var (client, project) = await SetupAsync();
        using var outsider = factory.CreateClient();
        var root = $"/api/projects/{project.Id}/feature-flags";
        Assert.Equal(HttpStatusCode.Unauthorized, (await outsider.PostAsJsonAsync(root + "/missing/explain", new { distinctId = "p" })).StatusCode);
        outsider.DefaultRequestHeaders.Add("X-Api-Key", project.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await outsider.PostAsJsonAsync(root + "/missing/explain", new { distinctId = "p" })).StatusCode);
        outsider.DefaultRequestHeaders.Remove("X-Api-Key");
        await TestAuth.AuthenticateAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync(root + "/evaluate-batch", new { distinctIds = new[] { "p" }, keys = new[] { "a" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync(root + "/missing/clone", new { key = "copy", name = "Copy" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root + "/missing/explain", new { distinctId = new string('x', 401) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(root + "/missing/explain", new { distinctId = "p" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root + "/missing/clone", new { key = "bad key", name = "Copy" })).StatusCode);
    }
}
