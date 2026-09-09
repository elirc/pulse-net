using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

public class PersonActivityTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private async Task<(HttpClient Client, ProjectResponse Project, Person Person)> SetupAsync()
    {
        var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Person activity" });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var person = new Person { ProjectId = project.Id };
        db.Persons.Add(person);
        await db.SaveChangesAsync();
        return (client, project, person);
    }

    [Fact]
    public async Task Timeline_PagesTimestampTiesExactlyOnce_AndBindsCursorToQuery()
    {
        var (client, project, person) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var time = DateTimeOffset.Parse("2026-03-01T10:00:00Z");
        var ids = Enumerable.Range(1, 3).Select(n => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}")).ToArray();
        db.Events.AddRange(ids.Select(id => new AnalyticsEvent { Id = id, ProjectId = project.Id, PersonId = person.Id,
            Name = "signup", DistinctId = "person", Timestamp = time, PropertiesJson = "{\"plan\":\"pro\"}" }));
        db.Events.Add(new AnalyticsEvent { ProjectId = Guid.NewGuid(), PersonId = person.Id, Name = "foreign", DistinctId = "p", Timestamp = time });
        db.Events.Add(new AnalyticsEvent { ProjectId = project.Id, PersonId = Guid.NewGuid(), Name = "other-person", DistinctId = "q", Timestamp = time });
        await db.SaveChangesAsync();
        var path = $"/api/projects/{project.Id}/persons/{person.Id}/events?limit=1";
        var seen = new List<Guid>();
        string? cursor = null;
        string? firstCursor = null;
        do
        {
            var page = (await client.GetFromJsonAsync<PersonTimelinePage>(path + (cursor is null ? "" : "&cursor=" + cursor)))!;
            Assert.Single(page.Events);
            Assert.Equal("pro", page.Events[0].Properties.GetProperty("plan").GetString());
            seen.Add(page.Events[0].Id);
            cursor = page.NextCursor;
            firstCursor ??= cursor;
        } while (cursor is not null);
        Assert.Equal(ids.Reverse(), seen);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "&event=another&cursor=" + firstCursor)).StatusCode);
        var otherPerson = new Person { ProjectId = project.Id };
        db.Persons.Add(otherPerson);
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/projects/{project.Id}/persons/{otherPerson.Id}/events?cursor={firstCursor}")).StatusCode);
        var wrongPurpose = new ScopedCursor(1, "export-history", project.Id, person.Id, null, time.UtcTicks, ids[0]).Encode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "&cursor=" + wrongPurpose)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "&cursor=invalid!")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "&cursor=" + new string('a', 2049))).StatusCode);
    }

    [Fact]
    public async Task Timeline_UsesCanonicalPersonAfterARealIdentityMerge()
    {
        var (client, project, _) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        var anon = await identity.ResolveAsync(project.Id, "device-A");
        var known = await identity.ResolveAsync(project.Id, "user-B");
        db.Events.AddRange(new AnalyticsEvent { ProjectId = project.Id, PersonId = anon.Id, Name = "browse", DistinctId = "device-A" },
            new AnalyticsEvent { ProjectId = project.Id, PersonId = known.Id, Name = "signup", DistinctId = "user-B" });
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var merged = await identity.IdentifyAsync(project.Id, "user-B", "device-A");
            Assert.Equal(known.Id, merged.Id);
            await transaction.CommitAsync();
        }
        var page = (await client.GetFromJsonAsync<PersonTimelinePage>($"/api/projects/{project.Id}/persons/{known.Id}/events"))!;
        Assert.Equal(2, page.Events.Count);
        Assert.Equal(new[] { "device-A", "user-B" }, page.Events.Select(e => e.DistinctId).Order());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{project.Id}/persons/{anon.Id}/events")).StatusCode);
    }

    [Fact]
    public async Task Summary_CountsProcessedCanonicalEvents_UsesUtcDaysAndExclusiveEnd()
    {
        var (client, project, person) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var from = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
        var to = from.AddDays(2);
        db.Events.AddRange(
            new AnalyticsEvent { ProjectId = project.Id, PersonId = person.Id, Name = "a", DistinctId = "device", Timestamp = from },
            new AnalyticsEvent { ProjectId = project.Id, PersonId = person.Id, Name = "a", DistinctId = "user", Timestamp = DateTimeOffset.Parse("2026-03-02T00:30:00+02:00") },
            new AnalyticsEvent { ProjectId = project.Id, PersonId = person.Id, Name = "b", DistinctId = "user", Timestamp = from.AddDays(1) },
            new AnalyticsEvent { ProjectId = project.Id, PersonId = person.Id, Name = "excluded", DistinctId = "user", Timestamp = to },
            new AnalyticsEvent { ProjectId = project.Id, PersonId = null, Name = "unlinked", DistinctId = "user", Timestamp = from },
            new AnalyticsEvent { ProjectId = Guid.NewGuid(), PersonId = person.Id, Name = "foreign", DistinctId = "user", Timestamp = from });
        await db.SaveChangesAsync();
        var result = (await client.GetFromJsonAsync<PersonActivitySummary>($"/api/projects/{project.Id}/persons/{person.Id}/activity-summary?from={from:O}&to={to:O}".Replace("+00:00", "Z")))!;
        Assert.Equal(3, result.TotalEvents);
        Assert.Equal(2, result.ActiveUtcDays);
        Assert.Equal(from, result.FirstEventAt);
        Assert.Equal(from.AddDays(1), result.LastEventAt);
        Assert.Equal(new[] { new NamedEventCount("a", 2), new NamedEventCount("b", 1) }, result.TopEvents);
    }

    [Theory]
    [InlineData("events")]
    [InlineData("activity-summary?from=2026-03-01T00:00:00Z&to=2026-03-02T00:00:00Z")]
    public async Task EmptyPerson_IsDistinctFromMissing_AndAccessIsScoped(string suffix)
    {
        var (client, project, person) = await SetupAsync();
        var root = $"/api/projects/{project.Id}/persons";
        var response = await client.GetAsync(root + $"/{person.Id}/{suffix}");
        response.EnsureSuccessStatusCode();
        if (suffix == "events") Assert.Empty((await response.Content.ReadFromJsonAsync<PersonTimelinePage>())!.Events);
        else
        {
            var summary = (await response.Content.ReadFromJsonAsync<PersonActivitySummary>())!;
            Assert.Equal(0, summary.TotalEvents);
            Assert.Equal(0, summary.ActiveUtcDays);
            Assert.Null(summary.FirstEventAt);
            Assert.Null(summary.LastEventAt);
            Assert.Empty(summary.TopEvents);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + $"/{Guid.NewGuid()}/{suffix}")).StatusCode);
        using var outsider = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await outsider.GetAsync(root + $"/{person.Id}/{suffix}")).StatusCode);
        await TestAuth.AuthenticateAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(root + $"/{person.Id}/{suffix}")).StatusCode);
        var other = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Other" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{other.Id}/persons/{person.Id}/{suffix}")).StatusCode);
    }

    [Theory]
    [InlineData("events?limit=0")]
    [InlineData("events?limit=201")]
    [InlineData("activity-summary")]
    [InlineData("activity-summary?from=2026-03-01T00:00:00Z&to=2026-03-01T00:00:00Z")]
    [InlineData("activity-summary?from=2026-03-01T00:00:00Z&to=2026-09-01T00:00:00Z")]
    public async Task InvalidBounds_AreRejected(string suffix)
    {
        var (client, project, person) = await SetupAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/projects/{project.Id}/persons/{person.Id}/{suffix}")).StatusCode);
    }
}
