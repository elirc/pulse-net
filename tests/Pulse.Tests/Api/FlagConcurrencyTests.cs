using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

public class FlagConcurrencyTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private async Task<(HttpClient Client, ProjectResponse Project, string Path, FeatureFlagResponse Flag, string ETag)> SetupAsync()
    {
        var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        var project = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Concurrency" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        var path = $"/api/projects/{project.Id}/feature-flags/test";
        var created = await client.PostAsJsonAsync($"/api/projects/{project.Id}/feature-flags", new { key = "test", type = "boolean", name = "original" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var flag = (await created.Content.ReadFromJsonAsync<FeatureFlagResponse>())!;
        Assert.Equal(1, flag.Revision);
        Assert.Equal($"\"flag-{flag.Id:D}-r1\"", created.Headers.ETag!.Tag);
        return (client, project, path, flag, created.Headers.ETag.Tag);
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string path, string? etag, HttpMethod? method = null, object? body = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Put, path) { Content = JsonContent.Create(body ?? new { name = "updated" }) };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    [Theory]
    [InlineData(null, 428)]
    [InlineData("*", 400)]
    [InlineData("garbage", 400)]
    [InlineData("W/\"flag-00000000-0000-0000-0000-000000000001-r1\"", 400)]
    [InlineData("\"flag-00000000-0000-0000-0000-000000000001-r1\"", 412)]
    [InlineData("\"flag-00000000-0000-0000-0000-000000000001-r1\",\"another\"", 400)]
    [InlineData("\"flag-00000000-0000-0000-0000-000000000001-r0\"", 400)]
    public async Task InvalidPreconditions_NeverChangeFlagAuditOrHistory(string? token, int status)
    {
        var setup = await SetupAsync();
        using var client = setup.Client;
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
            Assert.Equal(status, (int)(await ChangeAsync(client, setup.Path, token, method)).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var flag = await db.FeatureFlags.SingleAsync(f => f.Id == setup.Flag.Id);
        Assert.Equal(1, flag.Revision);
        Assert.Equal("original", flag.Name);
        Assert.Equal(1, await db.FlagVersions.CountAsync(v => v.FlagId == flag.Id));
        Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.ResourceId == flag.Id));
    }

    [Fact]
    public async Task TwoReaders_RejectStaleEdits_ThenPermitExplicitReconciliationAndNoOp()
    {
        var setup = await SetupAsync();
        using var client = setup.Client;
        var readA = await client.GetAsync(setup.Path);
        var readB = await client.GetAsync(setup.Path);
        var first = await ChangeAsync(client, setup.Path, readA.Headers.ETag!.Tag, body: new { name = "A", rolloutPercentage = 25 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(2, (await first.Content.ReadFromJsonAsync<FeatureFlagResponse>())!.Revision);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await ChangeAsync(client, setup.Path, readB.Headers.ETag!.Tag)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await ChangeAsync(client, setup.Path, readB.Headers.ETag.Tag, HttpMethod.Delete)).StatusCode);
        var fresh = await client.GetAsync(setup.Path);
        var merged = await ChangeAsync(client, setup.Path, fresh.Headers.ETag!.Tag, body: new { name = "A + B" });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        var current = (await merged.Content.ReadFromJsonAsync<FeatureFlagResponse>())!;
        Assert.Equal(3, current.Revision);
        Assert.Equal(25, current.RolloutPercentage);
        var noop = await ChangeAsync(client, setup.Path, merged.Headers.ETag!.Tag, body: new { });
        Assert.Equal(4, (await noop.Content.ReadFromJsonAsync<FeatureFlagResponse>())!.Revision);
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(client, setup.Path, noop.Headers.ETag!.Tag, HttpMethod.Delete)).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(0, await db.FlagVersions.CountAsync(v => v.FlagId == setup.Flag.Id));
        Assert.Equal(5, await db.AuditEntries.CountAsync(a => a.ResourceId == setup.Flag.Id));
    }

    [Fact]
    public async Task MissingAndForeignResources_AreScopedBeforePreconditions()
    {
        var setup = await SetupAsync();
        using var client = setup.Client;
        Assert.Equal(HttpStatusCode.NotFound, (await ChangeAsync(client, setup.Path + "-missing", null)).StatusCode);
        using var outsider = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeAsync(outsider, setup.Path, null)).StatusCode);
        await TestAuth.AuthenticateAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound, (await ChangeAsync(outsider, setup.Path, null)).StatusCode);
        var badConfig = await ChangeAsync(client, setup.Path, setup.ETag, body: new { rolloutPercentage = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, badConfig.StatusCode);
        var valid = await client.GetAsync(setup.Path);
        Assert.Equal(setup.ETag, valid.Headers.ETag!.Tag);
    }
}
