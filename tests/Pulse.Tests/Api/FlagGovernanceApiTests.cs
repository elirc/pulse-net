using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class FlagGovernanceApiTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    [Fact]
    public async Task HistoryRestoreAndSchedules_HaveScopedReadAndEditContracts()
    {
        using var admin = factory.CreateClient(); await TestAuth.AuthenticateAsync(admin);
        var project = (await (await admin.PostAsJsonAsync("/api/projects", new { name = "Governance API" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        var root = $"/api/projects/{project.Id}";
        var created = await admin.PostAsJsonAsync(root + "/feature-flags", new { key = "flag", type = "boolean", name = "original", rolloutPercentage = 5 });
        created.EnsureSuccessStatusCode(); var path = root + "/feature-flags/flag";
        using var change = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(new { name = "changed", rolloutPercentage = 75 }) };
        change.Headers.IfMatch.Add(created.Headers.ETag!); var changed = await admin.SendAsync(change); changed.EnsureSuccessStatusCode();
        var page = await admin.GetFromJsonAsync<JsonElement>(path + "/versions?limit=1");
        Assert.Equal(2, page.GetProperty("versions")[0].GetProperty("revision").GetInt64());
        Assert.Equal(2, page.GetProperty("nextBeforeRevision").GetInt64());
        var older = await admin.GetFromJsonAsync<JsonElement>(path + "/versions?limit=1&beforeRevision=2");
        Assert.Equal(1, older.GetProperty("versions")[0].GetProperty("revision").GetInt64());
        Assert.Equal(JsonValueKind.Null, older.GetProperty("nextBeforeRevision").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path + "/versions?limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await admin.PostAsJsonAsync(path + "/restore", new { targetRevision = 1 })).StatusCode);
        using var restore = new HttpRequestMessage(HttpMethod.Post, path + "/restore") { Content = JsonContent.Create(new { targetRevision = 1 }) };
        restore.Headers.IfMatch.Add(changed.Headers.ETag!); var restored = await admin.SendAsync(restore); restored.EnsureSuccessStatusCode();
        var flag = (await restored.Content.ReadFromJsonAsync<FeatureFlagResponse>())!;
        Assert.Equal(3, flag.Revision); Assert.Equal("original", flag.Name); Assert.Equal(5, flag.RolloutPercentage); Assert.NotNull(restored.Headers.ETag);
        using var member = factory.CreateClient(); var (token, email) = await TestAuth.RegisterAsync(member);
        member.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await admin.PostAsJsonAsync(root + "/members", new { email })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(path + "/versions")).StatusCode);
        var payload = new { executeAt = DateTimeOffset.UtcNow.AddHours(1), rolloutPercentage = 90, expectedRevision = 3 };
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(path + "/rollout-schedule", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(path + "/restore", new { targetRevision = 1 })).StatusCode);
        var scheduled = await admin.PostAsJsonAsync(path + "/rollout-schedule", payload);
        Assert.Equal(HttpStatusCode.Accepted, scheduled.StatusCode); Assert.EndsWith("/rollout-schedule", scheduled.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(path + "/rollout-schedule", payload)).StatusCode);
        var state = await member.GetFromJsonAsync<JsonElement>(path + "/rollout-schedule"); Assert.Equal("pending", state.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync(path + "/rollout-schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(path + "/rollout-schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync(path + "/rollout-schedule")).StatusCode);
        state = await member.GetFromJsonAsync<JsonElement>(path + "/rollout-schedule"); Assert.Equal("cancelled", state.GetProperty("status").GetString());
        using var outsider = factory.CreateClient(); await TestAuth.AuthenticateAsync(outsider);
        foreach (var suffix in new[] { "/versions", "/rollout-schedule" }) Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(path + suffix)).StatusCode);
    }
}
