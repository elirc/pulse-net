using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class ProjectRoleTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private async Task<(HttpClient Admin, HttpClient Member, ProjectResponse Project, Guid User)> TeamAsync()
    {
        var admin = factory.CreateClient();
        await TestAuth.AuthenticateAsync(admin);
        var project = (await (await admin.PostAsJsonAsync("/api/projects", new { name = "Roles" }))
            .Content.ReadFromJsonAsync<ProjectResponse>())!;
        var member = factory.CreateClient();
        var (token, email) = await TestAuth.RegisterAsync(member);
        member.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var invited = await admin.PostAsJsonAsync($"/api/projects/{project.Id}/members", new { email });
        invited.EnsureSuccessStatusCode();
        var row = (await invited.Content.ReadFromJsonAsync<MemberResponse>())!;
        Assert.Equal("viewer", row.Role);
        return (admin, member, project, row.UserId);
    }

    [Fact]
    public async Task NewViewer_SeesProjectWithoutCredentials_AndCanPreviewButCannotWrite()
    {
        var (admin, member, project, _) = await TeamAsync();
        using (admin) using (member)
        {
            var path = $"/api/projects/{project.Id}";
            foreach (var url in new[] { path, "/api/projects" })
            {
                var json = await member.GetStringAsync(url);
                Assert.DoesNotContain("apiKey", json);
                Assert.DoesNotContain("readKey", json);
                Assert.DoesNotContain(project.ApiKey, json);
                Assert.Contains("viewer", json);
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync(path, new { name = "No" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(path + "/dashboards", new { name = "No" })).StatusCode);
            var preview = await member.PostAsJsonAsync(path + "/insights/preview", new
            { type = "trend", config = new { @event = "signup", from = "2026-03-01T00:00:00Z", to = "2026-03-02T00:00:00Z" } });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using var outsider = factory.CreateClient();
            await TestAuth.AuthenticateAsync(outsider);
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task RoleChangesAndRemoval_AffectTheNextRequest_AndPersonalKeys()
    {
        var (admin, member, project, user) = await TeamAsync();
        using (admin) using (member)
        {
            var path = $"/api/projects/{project.Id}";
            var keyResponse = await member.PostAsJsonAsync("/api/personal-api-keys", new { name = "role-test" });
            keyResponse.EnsureSuccessStatusCode();
            var key = await keyResponse.Content.ReadFromJsonAsync<JsonElement>();
            using var keyClient = factory.CreateClient();
            keyClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.GetProperty("key").GetString());
            foreach (var client in new[] { member, keyClient })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/dashboards", new { name = "No" })).StatusCode);
            (await admin.PutAsJsonAsync(path + $"/members/{user}/role", new { role = "editor" })).EnsureSuccessStatusCode();
            foreach (var client in new[] { member, keyClient })
            {
                Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(path + "/dashboards", new { name = "Allowed" })).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/members", new { email = "someone@test.dev" })).StatusCode);
                Assert.DoesNotContain("apiKey", await client.GetStringAsync(path));
            }
            (await admin.DeleteAsync(path + $"/members/{user}")).EnsureSuccessStatusCode();
            foreach (var client in new[] { member, keyClient })
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task LastAdminAndInvalidRole_AreRejectedWithoutChangingMembership()
    {
        var (admin, member, project, user) = await TeamAsync();
        using (admin) using (member)
        {
            var path = $"/api/projects/{project.Id}";
            var members = (await admin.GetFromJsonAsync<List<MemberResponse>>(path + "/members"))!;
            var owner = members.Single(m => m.Role == "admin");
            Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync(path + $"/members/{owner.UserId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(path + $"/members/{owner.UserId}/role", new { role = "viewer" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(path + $"/members/{user}/role", new { role = "0" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync(path + $"/members/{Guid.NewGuid()}")).StatusCode);
            var json = await admin.GetStringAsync(path);
            Assert.Contains(project.ApiKey, json);
            Assert.Contains(project.ReadKey, json);
            Assert.Single((await admin.GetFromJsonAsync<List<MemberResponse>>(path + "/members"))!, m => m.Role == "admin");
        }
    }

    [Fact]
    public void EveryProjectRoute_HasAnExplicitPermissionClassification()
    {
        using var client = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/projects/{", StringComparison.OrdinalIgnoreCase) == true).ToList();
        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
                Assert.True(ProjectPermissionMatrix.Rules.ContainsKey(ProjectPermissionMatrix.Normalize(method, endpoint.RoutePattern.RawText!)),
                    $"Unclassified: {method} {endpoint.RoutePattern.RawText}");
    }
}
