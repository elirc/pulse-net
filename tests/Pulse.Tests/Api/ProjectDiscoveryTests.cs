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

public class ProjectDiscoveryTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
    private const string Range = "from=2026-03-01T00:00:00Z&to=2026-03-02T00:00:00Z";
    private async Task<(HttpClient Client, ProjectResponse Project)> SetupAsync()
    {
        var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Discovery" });
        response.EnsureSuccessStatusCode();
        return (client, (await response.Content.ReadFromJsonAsync<ProjectResponse>())!);
    }

    [Fact]
    public async Task EventUsage_CountsFullSetBeforeTopLimit_AndIgnoresUnusedRegistryNames()
    {
        var (client, project) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Events.AddRange(new[] { "signup", "signup", "signup", "purchase" }.Select(name => new AnalyticsEvent
        { ProjectId = project.Id, Name = name, DistinctId = "p", Timestamp = From }));
        db.Events.Add(new AnalyticsEvent { ProjectId = project.Id, Name = "boundary-excluded", DistinctId = "p", Timestamp = From.AddDays(1) });
        db.Events.Add(new AnalyticsEvent { ProjectId = Guid.NewGuid(), Name = "foreign", DistinctId = "p", Timestamp = From });
        db.EventDefinitions.Add(new EventDefinition { ProjectId = project.Id, Name = "unused" });
        await db.SaveChangesAsync();
        var usage = (await client.GetFromJsonAsync<EventUsage>($"/api/projects/{project.Id}/event-usage?{Range}&limit=1"))!;
        Assert.Equal(4, usage.TotalEvents);
        Assert.Equal(2, usage.EventNameCount);
        var top = Assert.Single(usage.Items);
        Assert.Equal(new EventNameUsage("signup", 3, From, From), top);
        await db.Events.Where(e => e.ProjectId == project.Id).ExecuteDeleteAsync();
        var empty = (await client.GetFromJsonAsync<EventUsage>($"/api/projects/{project.Id}/event-usage?{Range}"))!;
        Assert.Equal(0, empty.TotalEvents);
        Assert.Empty(empty.Items);
        Assert.Equal(1, await db.EventDefinitions.CountAsync(e => e.ProjectId == project.Id));
    }

    [Fact]
    public async Task PropertyValues_PreserveTypedCategoriesLiteralNamesAndSentinelLookingStrings()
    {
        var (client, project) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var json = new[] { "{\"plan\":\"pro\"}", "{\"plan\":\"Pro\"}", "{\"plan\":\"\"}", "{\"plan\":null}", "{}",
            "{\"plan\":1}", "{\"plan\":\"(other)\"}", "{\"plan\":\"1\"}", "{\"plan\":true}" };
        db.Events.AddRange(json.Select(properties => new AnalyticsEvent { ProjectId = project.Id, Name = "signup", DistinctId = "p", Timestamp = From, PropertiesJson = properties }));
        db.Events.Add(new AnalyticsEvent { ProjectId = Guid.NewGuid(), Name = "signup", DistinctId = "p", Timestamp = From, PropertiesJson = "{\"plan\":\"foreign\"}" });
        await db.SaveChangesAsync();
        var path = $"/api/projects/{project.Id}/property-values?event=signup&property=plan&{Range}";
        var all = (await client.GetFromJsonAsync<PropertyValues>(path))!;
        Assert.Equal(9, all.TotalEvents);
        Assert.Equal(1, all.MissingCount);
        Assert.Equal(1, all.NullCount);
        Assert.Equal(2, all.NonStringCount);
        Assert.Contains(all.Values, v => v.Value == "(other)");
        Assert.Contains(all.Values, v => v.Value == "1");
        Assert.Contains(all.Values, v => v.Value == "pro");
        Assert.Contains(all.Values, v => v.Value == "Pro");
        var top = (await client.GetFromJsonAsync<PropertyValues>(path + "&limit=1"))!;
        Assert.Equal("", Assert.Single(top.Values).Value);
        Assert.Equal(4, top.OtherStringCount);
        Assert.Equal(top.TotalEvents, top.Values.Sum(v => v.Count) + top.MissingCount + top.NullCount + top.NonStringCount + top.OtherStringCount);
        var literal = (await client.GetFromJsonAsync<PropertyValues>($"/api/projects/{project.Id}/property-values?event=signup&property=plan.name&{Range}"))!;
        Assert.Equal(9, literal.MissingCount);
    }

    [Fact]
    public async Task Overview_CountsEntitiesRatherThanAliasesOrTiles_AndNeverReturnsCredentials()
    {
        var (client, project) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var people = new[] { new Person { ProjectId = project.Id }, new Person { ProjectId = project.Id } };
        db.Persons.AddRange(people);
        db.PersonDistinctIds.AddRange(Enumerable.Range(0, 3).Select(i => new PersonDistinctId { ProjectId = project.Id, PersonId = people[i % 2].Id, DistinctId = "p" + i }));
        db.Events.AddRange(Enumerable.Range(0, 4).Select(i => new AnalyticsEvent { ProjectId = project.Id, PersonId = people[i % 2].Id, Name = "a", DistinctId = "p" + i }));
        var dashboard = new Dashboard { ProjectId = project.Id, Name = "One board" };
        var insight = new Insight { ProjectId = project.Id, Name = "One query" };
        db.Dashboards.Add(dashboard);
        db.Insights.Add(insight);
        db.DashboardTiles.AddRange(Enumerable.Range(0, 2).Select(_ => new DashboardTile { DashboardId = dashboard.Id, InsightId = insight.Id }));
        db.Cohorts.Add(new Cohort { ProjectId = project.Id, Name = "One audience" });
        db.FeatureFlags.AddRange(new FeatureFlag { ProjectId = project.Id, Key = "active-zero", Active = true, RolloutPercentage = 0 },
            new FeatureFlag { ProjectId = project.Id, Key = "inactive", Active = false });
        db.ExportJobs.AddRange(new ExportJob { ProjectId = project.Id, Type = "events", Format = "json", Status = ExportJobStatus.Completed, ResultContent = "private-document" },
            new ExportJob { ProjectId = project.Id, Type = "events", Format = "json", Status = ExportJobStatus.Failed });
        await db.SaveChangesAsync();
        var raw = await client.GetStringAsync($"/api/projects/{project.Id}/overview");
        Assert.DoesNotContain(project.ApiKey, raw);
        Assert.DoesNotContain(project.ReadKey, raw);
        Assert.DoesNotContain("apiKey", raw);
        Assert.DoesNotContain("readKey", raw);
        Assert.DoesNotContain("private-document", raw);
        var overview = JsonSerializer.Deserialize<ProjectOverview>(raw, JsonSerializerOptions.Web)!;
        Assert.Equal(new ProjectOverviewCounts(2, 4, 1, 1, 1, 2, 1, 1), overview.Counts);
        Assert.Equal(0, overview.Ingestion.Pending);
    }

    [Fact]
    public async Task EmptyOverviewAndStatus_ValidateOverridesAndEnforceProjectAccess()
    {
        var (client, project) = await SetupAsync();
        var path = $"/api/projects/{project.Id}";
        var overview = (await client.GetFromJsonAsync<ProjectOverview>(path + "/overview"))!;
        Assert.Equal(new ProjectOverviewCounts(0, 0, 0, 0, 0, 0, 0, 0), overview.Counts);
        var status = (await client.GetFromJsonAsync<IngestionStatus>(path + "/ingestion/status"))!;
        Assert.Equal("ok", status.Status);
        Assert.Empty(status.Reasons);
        Assert.Equal(new IngestionThresholds(), status.Thresholds);
        foreach (var suffix in new[] { "/ingestion/status?maxPending=0", "/ingestion/status?maxPendingAgeSeconds=3601", "/event-usage", "/property-values?event=a&property=p&" + Range + "&limit=26" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + suffix)).StatusCode);
        using var outsider = factory.CreateClient();
        foreach (var suffix in new[] { "/overview", "/ingestion/status", "/event-usage?" + Range, "/property-values?event=a&property=p&" + Range })
            Assert.Equal(HttpStatusCode.Unauthorized, (await outsider.GetAsync(path + suffix)).StatusCode);
        await TestAuth.AuthenticateAsync(outsider);
        foreach (var suffix in new[] { "/overview", "/ingestion/status", "/event-usage?" + Range, "/property-values?event=a&property=p&" + Range })
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(path + suffix)).StatusCode);
    }
}
