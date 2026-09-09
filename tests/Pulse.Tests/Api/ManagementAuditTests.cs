using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

public class ManagementAuditTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    [Fact]
    public async Task CoveredChanges_AttributeJwtAndPersonalKeyActors_AndRedactUserText()
    {
        using var admin = factory.CreateClient(); await TestAuth.AuthenticateAsync(admin);
        var project = (await (await admin.PostAsJsonAsync("/api/projects", new { name = "Audit" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        var root = $"/api/projects/{project.Id}";
        using var member = factory.CreateClient(); var (token, email) = await TestAuth.RegisterAsync(member);
        member.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var invitation = await admin.PostAsJsonAsync(root + "/members", new { email });
        var membership = (await invitation.Content.ReadFromJsonAsync<MemberResponse>())!;
        (await admin.PostAsJsonAsync(root + "/members", new { email })).EnsureSuccessStatusCode(); // idempotent: no extra row
        (await admin.PutAsJsonAsync(root + $"/members/{membership.UserId}/role", new { role = "editor" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(root + "/audit")).StatusCode);
        var keyResponse = await member.PostAsJsonAsync("/api/personal-api-keys", new { name = "audit client" });
        keyResponse.EnsureSuccessStatusCode();
        var key = await keyResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var keyClient = factory.CreateClient();
        keyClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.GetProperty("key").GetString());
        const string secret = "never-store-this-user-controlled-marker";
        var created = await member.PostAsJsonAsync(root + "/feature-flags", new { key = "audit", type = "boolean", name = secret });
        created.EnsureSuccessStatusCode(); var flag = (await created.Content.ReadFromJsonAsync<FeatureFlagResponse>())!;
        using var edit = new HttpRequestMessage(HttpMethod.Put, root + "/feature-flags/audit") { Content = JsonContent.Create(new { name = secret + "-edit" }) };
        edit.Headers.IfMatch.Add(created.Headers.ETag!);
        var updated = await keyClient.SendAsync(edit); updated.EnsureSuccessStatusCode();
        (await admin.DeleteAsync(root + $"/members/{membership.UserId}")).EnsureSuccessStatusCode();
        var json = await admin.GetStringAsync(root + "/audit");
        Assert.DoesNotContain(secret, json); Assert.DoesNotContain(email, json); Assert.DoesNotContain(project.ApiKey, json);
        Assert.DoesNotContain(key.GetProperty("key").GetString()!, json);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var entries = await db.AuditEntries.Where(a => a.ProjectId == project.Id).OrderBy(a => a.Sequence).ToListAsync();
        Assert.Equal(new[] { "member.added", "member.added", "member.role_changed", "flag.created", "flag.updated", "member.removed" }, entries.Select(a => a.Action));
        var createdAudit = entries.Single(a => a.Action == "flag.created"); Assert.Null(createdAudit.PersonalKeyId);
        var updatedAudit = entries.Single(a => a.Action == "flag.updated");
        Assert.Equal(membership.UserId, updatedAudit.ActorUserId); Assert.Equal(flag.Id, updatedAudit.ResourceId); Assert.NotNull(updatedAudit.PersonalKeyId);
        Assert.Equal(createdAudit.ActorUserId, updatedAudit.ActorUserId);
        Assert.All(entries, entry => Assert.DoesNotContain(secret, entry.SummaryJson));
        var count = entries.Count;
        Assert.Equal(HttpStatusCode.NotFound, (await keyClient.PostAsJsonAsync(root + "/feature-flags", new { key = "denied", type = "boolean" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(root + "/feature-flags", new { key = "bad key", type = "boolean" })).StatusCode);
        Assert.Equal(count, await db.AuditEntries.CountAsync(a => a.ProjectId == project.Id));
    }

    [Fact]
    public async Task AuditPagination_UsesSequenceAndBindsProjectActionAndPurpose()
    {
        using var client = factory.CreateClient(); await TestAuth.AuthenticateAsync(client);
        var project = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Audit pages" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        var root = $"/api/projects/{project.Id}";
        for (var i = 0; i < 3; i++) (await client.PostAsJsonAsync(root + "/feature-flags", new { key = $"f{i}", type = "boolean" })).EnsureSuccessStatusCode();
        var seen = new List<long>(); string? cursor = null, firstCursor = null;
        do
        {
            var page = await client.GetFromJsonAsync<JsonElement>(root + "/audit?action=flag.created&limit=1" + (cursor is null ? "" : "&cursor=" + cursor));
            var entry = Assert.Single(page.GetProperty("entries").EnumerateArray());
            Assert.Equal("flag.created", entry.GetProperty("action").GetString()); Assert.Equal(project.Id, entry.GetProperty("projectId").GetGuid());
            seen.Add(entry.GetProperty("sequence").GetInt64()); cursor = page.GetProperty("nextCursor").GetString(); firstCursor ??= cursor;
        } while (cursor is not null);
        Assert.Equal(3, seen.Count); Assert.Equal(seen.OrderDescending(), seen); Assert.Equal(3, seen.Distinct().Count());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(root + $"/audit?action=flag.updated&cursor={firstCursor}")).StatusCode);
        foreach (var query in new[] { "limit=201", "limit=0", "action=unknown", "cursor=broken", "cursor=" + new string('x', 2049) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(root + "/audit?" + query)).StatusCode);
        var another = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Another" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/projects/{another.Id}/audit?action=flag.created&cursor={firstCursor}")).StatusCode);
        using var outsider = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await outsider.GetAsync(root + "/audit")).StatusCode);
        await TestAuth.AuthenticateAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(root + "/audit")).StatusCode);
    }
}
