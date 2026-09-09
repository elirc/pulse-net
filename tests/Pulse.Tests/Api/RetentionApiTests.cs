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

public class RetentionApiTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    [Fact]
    public async Task AdminCanPreviewAndConditionallyEdit_EditorsAndForeignMembersCannot()
    {
        using var client = factory.CreateClient(); await TestAuth.AuthenticateAsync(client);
        var project = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Retention" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        var root = $"/api/projects/{project.Id}/retention";
        var initial = (await client.GetFromJsonAsync<ProjectRetentionPolicy>(root))!; Assert.False(initial.Enabled); Assert.Equal(1, initial.Revision);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Events.Add(new() { ProjectId = project.Id, Name = "old", DistinctId = "p", Timestamp = DateTimeOffset.UtcNow.AddDays(-400) }); await db.SaveChangesAsync();
        var preview = (await client.GetFromJsonAsync<RetentionPreview>(root + "/preview"))!; Assert.Equal(1, preview.EligibleCount); Assert.False(preview.Enabled);
        Assert.Equal((HttpStatusCode)428, (await client.PutAsJsonAsync(root, new { enabled = true, days = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(root, new { enabled = true, days = 29, expectedRevision = 1 })).StatusCode);
        var updated = await client.PutAsJsonAsync(root, new { enabled = true, days = 30, expectedRevision = 1 }); updated.EnsureSuccessStatusCode();
        Assert.Equal(2, (await updated.Content.ReadFromJsonAsync<ProjectRetentionPolicy>())!.Revision);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.PutAsJsonAsync(root, new { enabled = false, days = 365, expectedRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(root + "/runs")).StatusCode);
        Assert.Equal(1, await db.Events.CountAsync(e => e.ProjectId == project.Id));
        using var foreign = factory.CreateClient(); await TestAuth.AuthenticateAsync(foreign);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(root)).StatusCode);
        await db.ProjectMemberships.Where(m => m.ProjectId == project.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ProjectRole.Editor));
        foreach (var suffix in new[] { "", "/preview", "/runs" }) Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(root + suffix)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(root, new { enabled = false, days = 365, expectedRevision = 2 })).StatusCode);
    }
}
