using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pulse.Api.Contracts;
using Pulse.Api.Ingestion;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

public class CaptureRecoveryTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private static async Task<ProjectResponse> ProjectAsync(HttpClient client)
    {
        await TestAuth.AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Capture recovery" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
    }

    [Fact]
    public async Task CapturePreview_MatchesQueuedNormalization_WithoutWritingPreviewData()
    {
        await using var owner = new PulseApiFactory();
        await using var server = owner.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var worker = services.Single(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(IngestionWorker));
            services.Remove(worker);
        }));
        using var client = server.CreateClient();
        var project = await ProjectAsync(client);
        var batch = Enumerable.Range(0, 4).Select(i => new { @event = " signup ", distinct_id = " p" + i + " ", properties = (object)new[] { "nonobject" } }).ToArray();
        var preview = await client.PostAsJsonAsync($"/api/projects/{project.Id}/capture/validate", new { @event = "ignored", distinct_id = "ignored", batch });
        preview.EnsureSuccessStatusCode();
        Assert.True(preview.Headers.CacheControl!.NoStore);
        var value = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, value.GetProperty("eventCount").GetInt32());
        Assert.Equal(3, value.GetProperty("preview").GetArrayLength());
        Assert.True(value.GetProperty("previewTruncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("preview")[0].GetProperty("timestamp").ValueKind);
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(0, await db.QueuedEvents.CountAsync());
        Assert.Equal(0, await db.Events.CountAsync());
        Assert.Equal(0, await db.Persons.CountAsync());
        Assert.Equal(0, await db.EventDefinitions.CountAsync());
        Assert.Equal(0, await db.PropertyDefinitions.CountAsync());
        var capture = await client.PostAsJsonAsync("/capture", new { api_key = project.ApiKey, batch });
        Assert.Equal(HttpStatusCode.Accepted, capture.StatusCode);
        var queued = await db.QueuedEvents.OrderBy(q => q.Seq).ToListAsync();
        Assert.Equal(4, queued.Count);
        for (var i = 0; i < 3; i++)
        {
            var incoming = JsonSerializer.Deserialize<IncomingEvent>(queued[i].PayloadJson)!;
            var row = value.GetProperty("preview")[i];
            Assert.Equal(incoming.Name, row.GetProperty("event").GetString());
            Assert.Equal(incoming.DistinctId, row.GetProperty("distinctId").GetString());
            Assert.Equal("{}", incoming.PropertiesJson);
            Assert.Equal(incoming.PropertiesJson, row.GetProperty("properties").GetRawText());
            Assert.Null(incoming.Timestamp);
        }
    }

    [Fact]
    public async Task CapturePreview_RejectsCredentialsNullBatchItemsAndOversizeWithoutWrites()
    {
        using var client = factory.CreateClient();
        var project = await ProjectAsync(client);
        var path = $"/api/projects/{project.Id}/capture/validate";
        foreach (var json in new[] { "{\"api_key\":null,\"event\":\"a\",\"distinct_id\":\"p\"}", "{\"batch\":[null]}", "{\"batch\":[{\"event\":\"ok\",\"distinct_id\":\"p\"},{}]}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, JsonSerializer.Deserialize<JsonElement>(json))).StatusCode);
        var badCapture = JsonSerializer.SerializeToElement(new { api_key = project.ApiKey, batch = new object?[] { null } });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/capture", badCapture)).StatusCode);
        var huge = new { @event = "a", distinct_id = "p", properties = new { text = new string('x', 1024 * 1024) } };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsJsonAsync(path, huge)).StatusCode);
        using var outsider = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await outsider.PostAsJsonAsync(path, new { @event = "a", distinct_id = "p" })).StatusCode);
        await TestAuth.AuthenticateAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync(path, new { @event = "a", distinct_id = "p" })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(0, await db.QueuedEvents.CountAsync(q => q.ProjectId == project.Id));
        Assert.Equal(0, await db.Events.CountAsync(q => q.ProjectId == project.Id));
        Assert.Equal(0, await db.Persons.CountAsync(q => q.ProjectId == project.Id));
    }

    [Fact]
    public async Task ReplayCheckAndMixedBatch_AgreeOnAdmission_PreserveOrderAndRetainInvalidForeignLetters()
    {
        using var client = factory.CreateClient();
        var project = await ProjectAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var good = new DeadLetterEvent { ProjectId = project.Id, Error = "old", PayloadJson = JsonSerializer.Serialize(new IncomingEvent("recovered", "p", null, "{}")) };
        var bad = new DeadLetterEvent { ProjectId = project.Id, Error = "invalid", PayloadJson = "{}" };
        var foreign = new DeadLetterEvent { ProjectId = Guid.NewGuid(), Error = "foreign", PayloadJson = good.PayloadJson };
        db.DeadLetterEvents.AddRange(good, bad, foreign);
        await db.SaveChangesAsync();
        var root = $"/api/projects/{project.Id}/ingestion/dead-letters";
        var check = await client.GetFromJsonAsync<JsonElement>(root + $"/{good.Id}/replay-check");
        Assert.True(check.GetProperty("replayable").GetBoolean());
        Assert.Empty(check.GetProperty("issues").EnumerateArray());
        Assert.Equal(0, await db.QueuedEvents.CountAsync(q => q.ProjectId == project.Id));
        var invalid = await client.GetFromJsonAsync<JsonElement>(root + $"/{bad.Id}/replay-check");
        Assert.False(invalid.GetProperty("replayable").GetBoolean());
        Assert.Equal(3, invalid.GetProperty("issues").GetArrayLength());
        var ids = new[] { good.Id, bad.Id, Guid.NewGuid(), foreign.Id };
        var response = await client.PostAsJsonAsync(root + "/replay-batch", new { letterIds = ids });
        response.EnsureSuccessStatusCode();
        var batch = (await response.Content.ReadFromJsonAsync<ReplayBatchResult>())!;
        Assert.Equal(ids, batch.Results.Select(r => r.LetterId));
        Assert.Equal(new[] { "queued", "invalidPayload", "notFound", "notFound" }, batch.Results.Select(r => r.Outcome));
        Assert.Equal((1, 1, 2), (batch.Queued, batch.Invalid, batch.NotFound));
        await TestIngestion.WaitForDrainAsync(client);
        Assert.Equal(1, await db.Events.CountAsync(e => e.ProjectId == project.Id && e.Name == "recovered"));
        Assert.True(await db.DeadLetterEvents.AnyAsync(d => d.Id == bad.Id));
        Assert.True(await db.DeadLetterEvents.AnyAsync(d => d.Id == foreign.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + $"/{good.Id}/replay-check")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + $"/{foreign.Id}/replay-check")).StatusCode);
        var repeated = await client.PostAsJsonAsync(root + "/replay-batch", new { letterIds = new[] { good.Id } });
        Assert.Equal(1, (await repeated.Content.ReadFromJsonAsync<ReplayBatchResult>())!.NotFound);
    }

    [Fact]
    public async Task DuplicateReplayList_DoesNotConsumeAnyLetter()
    {
        using var client = factory.CreateClient();
        var project = await ProjectAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var letter = new DeadLetterEvent { ProjectId = project.Id, Error = "old", PayloadJson = JsonSerializer.Serialize(new IncomingEvent("a", "p", null, "{}")) };
        db.DeadLetterEvents.Add(letter);
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/projects/{project.Id}/ingestion/dead-letters/replay-batch", new { letterIds = new[] { letter.Id, letter.Id } })).StatusCode);
        Assert.True(await db.DeadLetterEvents.AnyAsync(d => d.Id == letter.Id));
        Assert.Equal(0, await db.QueuedEvents.CountAsync(q => q.ProjectId == project.Id));
    }
}
