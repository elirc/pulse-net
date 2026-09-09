using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class ProjectReadKeyRotationTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    [Fact]
    public async Task Rotation_ChangesOnlyReadCapability_RejectsStaleExpectedKey_AndDoesNotCacheSecrets()
    {
        using var admin = factory.CreateClient();
        await TestAuth.AuthenticateAsync(admin);
        var project = (await (await admin.PostAsJsonAsync("/api/projects", new { name = "Rotate" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        var path = $"/api/projects/{project.Id}";
        var query = path + "/insights/trend?event=rotation-test&from=2026-03-01T00:00:00Z&to=2026-03-02T00:00:00Z";
        using var reader = factory.CreateClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", project.ReadKey);
        (await reader.GetAsync(query)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.PostAsJsonAsync(path + "/read-key/rotate", new { expectedReadKey = project.ReadKey })).StatusCode);
        var rotate = await admin.PostAsJsonAsync(path + "/read-key/rotate", new { expectedReadKey = project.ReadKey });
        rotate.EnsureSuccessStatusCode();
        Assert.True(rotate.Headers.CacheControl!.NoStore);
        var next = (await rotate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("readKey").GetString()!;
        Assert.NotEqual(project.ReadKey, next);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync(query)).StatusCode);
        reader.DefaultRequestHeaders.Remove("X-Api-Key");
        reader.DefaultRequestHeaders.Add("X-Api-Key", next);
        (await reader.GetAsync(query)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(path + "/read-key/rotate", new { expectedReadKey = project.ReadKey })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(path + "/read-key/rotate", new { expectedReadKey = "bad" })).StatusCode);
        var current = (await admin.GetFromJsonAsync<ProjectResponse>(path))!;
        Assert.Equal(next, current.ReadKey);
        Assert.Equal(project.ApiKey, current.ApiKey);
        Assert.Equal(project.Name, current.Name);
        Assert.Equal(project.CreatedAt, current.CreatedAt);
        (await admin.PostAsJsonAsync("/capture", new { api_key = project.ApiKey, @event = "rotation-test", distinct_id = "p", timestamp = "2026-03-01T12:00:00Z" })).EnsureSuccessStatusCode();
        await TestIngestion.WaitForDrainAsync(admin);
        var trend = await reader.GetFromJsonAsync<JsonElement>(query);
        Assert.Equal(1, trend.GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task TwoRotationsWithSameExpectation_CannotOverwriteTheWinner()
    {
        using var admin = factory.CreateClient();
        await TestAuth.AuthenticateAsync(admin);
        var project = (await (await admin.PostAsJsonAsync("/api/projects", new { name = "Competing rotations" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        var path = $"/api/projects/{project.Id}";
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => admin.PostAsJsonAsync(path + "/read-key/rotate", new { expectedReadKey = project.ReadKey })));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var winner = await responses.Single(r => r.IsSuccessStatusCode).Content.ReadFromJsonAsync<JsonElement>();
        var current = (await admin.GetFromJsonAsync<ProjectResponse>(path))!;
        Assert.Equal(winner.GetProperty("readKey").GetString(), current.ReadKey);
    }
}
