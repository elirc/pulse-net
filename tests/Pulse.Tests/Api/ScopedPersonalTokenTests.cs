using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Tests.Infrastructure;

namespace Pulse.Tests.Api;

public class ScopedPersonalTokenTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(129600)]
    public async Task ExactExpiryAdmissionBounds_AreAccepted(int minutes)
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var project = await f.ProjectAsync();
        var expires = f.Clock.GetUtcNow().AddMinutes(minutes);
        var response = await f.Jwt.PostAsJsonAsync("/api/personal-api-keys/restricted", new { name = "boundary", projectIds = new[] { project.Id }, scopes = new[] { "analytics:read" }, expiresAt = expires });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(expires, (await response.Content.ReadFromJsonAsync<PersonalApiKeyCreatedResponse>())!.ExpiresAt);
    }

    [Fact]
    public async Task NamedScopesDescribeCapabilities_NotHttpVerbsOrImplicitReadInheritance()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var project = await f.ProjectAsync(); var root = $"/api/projects/{project.Id}";
        var analyticsKey = await f.KeyAsync(project.Id, "analytics:read"); using var analytics = f.Client(analyticsKey.Key);
        Assert.Equal(HttpStatusCode.OK, (await analytics.PostAsJsonAsync(root + "/insights/preview", new { type = "trend", config = new { @event = "pageview", interval = "day" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analytics.PostAsJsonAsync(root + "/exports", new { type = "events" })).StatusCode);
        var exportKey = await f.KeyAsync(project.Id, "exports:write"); using var exports = f.Client(exportKey.Key);
        var create = await exports.PostAsJsonAsync(root + "/exports", new { type = "events" }); Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var job = (await create.Content.ReadFromJsonAsync<ExportJobResponse>())!;
        Assert.Equal(HttpStatusCode.Forbidden, (await exports.GetAsync(root + "/exports/" + job.Id)).StatusCode);
        var writeKey = await f.KeyAsync(project.Id, "configuration:write"); using var write = f.Client(writeKey.Key);
        Assert.Equal(HttpStatusCode.Created, (await write.PostAsJsonAsync(root + "/feature-flags", new { key = "scope-test", type = "boolean" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await write.GetAsync(root + "/feature-flags")).StatusCode);
        var readKey = await f.KeyAsync(project.Id, "configuration:read"); using var read = f.Client(readKey.Key);
        Assert.Equal(HttpStatusCode.OK, (await read.PostAsJsonAsync(root + "/feature-flags/scope-test/explain", new { distinctId = "p" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync(root + "/feature-flags/scope-test/clone", new { key = "forbidden", name = "Forbidden" })).StatusCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PulseApiFactory parent = new();
        public GovernanceClock Clock { get; } = new() { Now = DateTimeOffset.UtcNow };
        public WebApplicationFactory<Program> Server { get; }
        public HttpClient Jwt { get; }
        public Fixture()
        {
            Server = parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock); }));
            Jwt = Server.CreateClient();
        }
        public async Task InitializeAsync() => await TestAuth.AuthenticateAsync(Jwt);
        public async Task<ProjectResponse> ProjectAsync() => (await (await Jwt.PostAsJsonAsync("/api/projects", new { name = "Scoped " + Guid.NewGuid() })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        public async Task<PersonalApiKeyCreatedResponse> KeyAsync(Guid project, params string[] scopes)
        {
            var response = await Jwt.PostAsJsonAsync("/api/personal-api-keys/restricted", new { name = "automation", projectIds = new[] { project }, scopes, expiresAt = Clock.GetUtcNow().AddMinutes(5) });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            return (await response.Content.ReadFromJsonAsync<PersonalApiKeyCreatedResponse>())!;
        }
        public HttpClient Client(string key) { var client = Server.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", key); return client; }
        public async ValueTask DisposeAsync() { Jwt.Dispose(); await Server.DisposeAsync(); await parent.DisposeAsync(); }
    }

    [Fact]
    public async Task ProjectScopeAndCurrentRoleIntersect_AndCredentialsNeverLeak()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var a = await f.ProjectAsync(); var b = await f.ProjectAsync();
        var key = await f.KeyAsync(a.Id, "configuration:read", "configuration:write"); using var token = f.Client(key.Key);
        var listed = await token.GetStringAsync("/api/projects"); Assert.DoesNotContain(a.ApiKey, listed); Assert.DoesNotContain(a.ReadKey, listed); Assert.DoesNotContain(b.Id.ToString(), listed);
        var project = await token.GetStringAsync($"/api/projects/{a.Id}"); Assert.DoesNotContain(a.ApiKey, project); Assert.DoesNotContain(a.ReadKey, project);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync($"/api/projects/{a.Id}/feature-flags")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await token.GetAsync($"/api/projects/{b.Id}/feature-flags")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync($"/api/projects/{a.Id}/persons")).StatusCode);
        var created = await token.PostAsJsonAsync($"/api/projects/{a.Id}/feature-flags", new { key = "scoped-flag", name = "Scoped", type = "boolean" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var scope = f.Server.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Contains(await db.AuditEntries.Where(e => e.ProjectId == a.Id).ToListAsync(), entry => entry.PersonalKeyId == key.Id);
        Assert.Equal(ApiKeyGenerator.Sha256(key.Key), (await db.PersonalApiKeys.SingleAsync()).KeyHash);
        await db.ProjectMemberships.Where(m => m.ProjectId == a.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ProjectRole.Viewer));
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync($"/api/projects/{a.Id}/feature-flags", new { key = "denied", type = "boolean" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync($"/api/projects/{a.Id}/feature-flags")).StatusCode);
        await db.ProjectMemberships.Where(m => m.ProjectId == a.Id).ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await token.GetAsync($"/api/projects/{a.Id}/feature-flags")).StatusCode);
        Assert.Equal("[]", await token.GetStringAsync("/api/projects"));
    }

    [Fact]
    public async Task EveryMatrixRoute_RejectsDisallowedProject_AndAdminRoutesRejectAllScopes()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var a = await f.ProjectAsync(); var b = await f.ProjectAsync();
        var key = await f.KeyAsync(a.Id, RestrictedToken.KnownScopes); using var token = f.Client(key.Key);
        foreach (var (signature, rule) in ProjectPermissionMatrix.Rules)
        {
            var split = signature.IndexOf(' '); var method = signature[..split]; var template = signature[(split + 1)..];
            string Path(Guid project) { var at = template.IndexOf("{}", StringComparison.Ordinal); return (template[..at] + project + template[(at + 2)..]).Replace("{}", Guid.NewGuid().ToString()); }
            using var foreign = new HttpRequestMessage(new HttpMethod(method), Path(b.Id)) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            Assert.Equal(HttpStatusCode.NotFound, (await token.SendAsync(foreign)).StatusCode);
            if (rule.Scope is null)
            {
                using var admin = new HttpRequestMessage(new HttpMethod(method), Path(a.Id)) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                Assert.Equal(HttpStatusCode.Forbidden, (await token.SendAsync(admin)).StatusCode);
            }
        }
        foreach (var path in new[] { "/api/ingestion/metrics", "/api/personal-api-keys", "/api/personal-api-keys/restricted", "/api/projects" })
            Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync(path, new { name = "escape" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync("/api/personal-api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.DeleteAsync("/api/personal-api-keys/" + key.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ExpiryAtEqualityAndRevocationApplyOnNextRequest_AndLegacyCannotManageKeys()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var project = await f.ProjectAsync(); var key = await f.KeyAsync(project.Id, "configuration:read");
        using var token = f.Client(key.Key); var path = $"/api/projects/{project.Id}/feature-flags";
        f.Clock.Now = key.ExpiresAt!.Value.AddTicks(-1); Assert.Equal(HttpStatusCode.OK, (await token.GetAsync(path)).StatusCode);
        f.Clock.Advance(TimeSpan.FromTicks(1)); Assert.Equal(HttpStatusCode.Unauthorized, (await token.GetAsync(path)).StatusCode);
        var second = await f.KeyAsync(project.Id, "configuration:read"); using var revoked = f.Client(second.Key);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Jwt.DeleteAsync("/api/personal-api-keys/" + second.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.GetAsync(path)).StatusCode);
        var legacyResponse = await f.Jwt.PostAsJsonAsync("/api/personal-api-keys", new { name = "legacy" }); Assert.True(legacyResponse.Headers.CacheControl!.NoStore);
        var legacy = (await legacyResponse.Content.ReadFromJsonAsync<PersonalApiKeyCreatedResponse>())!; Assert.Equal("legacyUnrestricted", legacy.Mode); Assert.Null(legacy.ExpiresAt);
        using var old = f.Client(legacy.Key); Assert.Equal(HttpStatusCode.OK, (await old.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await old.PostAsJsonAsync("/api/personal-api-keys", new { name = "no minting" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await old.GetAsync("/api/personal-api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await old.DeleteAsync("/api/personal-api-keys/" + legacy.Id)).StatusCode);
    }

    [Theory]
    [InlineData("empty-projects")]
    [InlineData("too-many-projects")]
    [InlineData("unknown-project")]
    [InlineData("empty-scopes")]
    [InlineData("unknown-scope")]
    [InlineData("expiry-too-soon")]
    [InlineData("expiry-too-late")]
    public async Task InvalidRestrictionRequests_CreateNoKeyOrMappings(string scenario)
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var project = await f.ProjectAsync();
        var projects = scenario switch { "empty-projects" => Array.Empty<Guid>(), "too-many-projects" => Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray(), "unknown-project" => [Guid.NewGuid()], _ => new[] { project.Id } };
        var scopes = scenario switch { "empty-scopes" => Array.Empty<string>(), "unknown-scope" => ["admin"], _ => new[] { "analytics:read" } };
        var expires = scenario switch { "expiry-too-soon" => f.Clock.GetUtcNow().AddSeconds(59), "expiry-too-late" => f.Clock.GetUtcNow().AddDays(90).AddTicks(1), _ => f.Clock.GetUtcNow().AddMinutes(1) };
        var response = await f.Jwt.PostAsJsonAsync("/api/personal-api-keys/restricted", new { name = "invalid", projectIds = projects, scopes, expiresAt = expires });
        Assert.Equal(scenario == "unknown-project" ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = f.Server.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Empty(await db.PersonalApiKeys.ToListAsync()); Assert.Empty(await db.PersonalKeyProjects.ToListAsync()); Assert.Empty(await db.PersonalKeyScopes.ToListAsync());
    }
}
