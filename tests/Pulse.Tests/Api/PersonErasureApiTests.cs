using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

public class PersonErasureApiTests
{
    [Fact]
    public async Task PauseBlocksDataAndCapture_AdminCanInspectAndResume_WorkerCompletionRestoresAccess()
    {
        await using var parent = new PulseApiFactory();
        using var factory = parent.WithWebHostBuilder(b => b.UseSetting("Erasure:WorkerEnabled", "false"));
        using var client = factory.CreateClient(); await TestAuth.AuthenticateAsync(client);
        var project = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Erasure boundaries" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        Guid personId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var person = new Person { ProjectId = project.Id }; personId = person.Id;
            db.Persons.Add(person); db.PersonDistinctIds.Add(new() { ProjectId = project.Id, PersonId = person.Id, DistinctId = "secret-known-alias" }); await db.SaveChangesAsync();
        }
        var root = $"/api/projects/{project.Id}";
        var response = await client.PostAsync(root + $"/persons/{personId}/erasure", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>()); var statusUrl = body.GetProperty("statusUrl").GetString()!;
        var jobId = body.GetProperty("job").GetProperty("id").GetGuid();
        Assert.True(body.GetProperty("invalidatesAllProjectExports").GetBoolean());
        foreach (var path in new[] { "/persons", $"/persons/{personId}", "/insights/trend?event=e", "/exports", "/dashboards", "/cohorts" })
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(root + path)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/capture", new { api_key = project.ApiKey, @event = "e", distinct_id = "other" })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/decide", new { api_key = project.ApiKey, distinct_id = "other" })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync(root + "/cohorts", new { name = "Cannot write", type = "static" })).StatusCode);
        using var reader = factory.CreateClient(); reader.DefaultRequestHeaders.Add("X-Api-Key", project.ReadKey);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await reader.GetAsync(root + "/insights/trend?event=e")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(statusUrl)).StatusCode);
        Assert.DoesNotContain("secret-known-alias", await (await client.GetAsync(statusUrl)).Content.ReadAsStringAsync());
        using var foreign = factory.CreateClient(); await TestAuth.AuthenticateAsync(foreign);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(statusUrl)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            await db.ProjectMemberships.Where(m => m.ProjectId == project.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ProjectRole.Editor));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(statusUrl)).StatusCode);
            await db.ProjectMemberships.Where(m => m.ProjectId == project.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ProjectRole.Admin));
        }
        for (var i = 0; i < 20; i++)
        {
            using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            if ((await db.ErasureJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).Status == ErasureJobStatus.Completed) break;
            await scope.ServiceProvider.GetRequiredService<PersonErasureService>().ProcessAsync(jobId, default);
        }
        Assert.Equal("completed", (await client.GetFromJsonAsync<JsonElement>(statusUrl)).GetProperty("job").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(root + "/persons")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + $"/persons/{personId}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/capture", new { api_key = project.ApiKey, @event = "e", distinct_id = "secret-known-alias" })).StatusCode);
    }
}
