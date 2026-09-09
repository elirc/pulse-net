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

public class AnalyticsCompositionTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private static readonly DateTimeOffset From = new(2026, 3, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(7);
    private async Task<(HttpClient Client, ProjectResponse Project)> SetupAsync()
    {
        var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Analytics composition" });
        response.EnsureSuccessStatusCode();
        return (client, (await response.Content.ReadFromJsonAsync<ProjectResponse>())!);
    }
    private static string Bounds(DateTimeOffset from, DateTimeOffset to) => $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    [Theory]
    [InlineData(15, 10, 5, 50d)]
    [InlineData(0, 0, 0, null)]
    [InlineData(0, 10, -10, -100d)]
    [InlineData(2, 3, -1, -33.33d)]
    public async Task Comparison_UsesLongCountsAndDefinedPercentage(int current, int previous, long delta, double? percent)
    {
        var (client, project) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Events.AddRange(Enumerable.Range(0, current).Select(_ => new AnalyticsEvent { ProjectId = project.Id, Name = "signup", DistinctId = "current", Timestamp = From.AddHours(1) }));
        db.Events.AddRange(Enumerable.Range(0, previous).Select(_ => new AnalyticsEvent { ProjectId = project.Id, Name = "signup", DistinctId = "previous", Timestamp = From.AddHours(-1) }));
        await db.SaveChangesAsync();
        var result = (await client.GetFromJsonAsync<PeriodComparison>($"/api/projects/{project.Id}/insights/period-comparison?event=signup&{Bounds(From, To)}"))!;
        Assert.Equal(current, result.CurrentCount);
        Assert.Equal(previous, result.PreviousCount);
        Assert.Equal(delta, result.Delta);
        Assert.Equal(percent, result.PercentChange);
        Assert.Equal(From.AddDays(-7), result.PreviousFrom);
        Assert.Equal(From, result.PreviousTo);
    }

    [Fact]
    public async Task Comparison_HalfOpenBoundaries_IsolationAndTimezoneEquivalence()
    {
        var (client, project) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Events.AddRange(new[] { From.AddDays(-7), From.AddTicks(-1), From, To.AddTicks(-1), To }.Select(time =>
            new AnalyticsEvent { ProjectId = project.Id, Name = "signup", DistinctId = "p", Timestamp = time }));
        db.Events.Add(new AnalyticsEvent { ProjectId = Guid.NewGuid(), Name = "signup", DistinctId = "foreign", Timestamp = From });
        db.Events.Add(new AnalyticsEvent { ProjectId = project.Id, Name = "other", DistinctId = "p", Timestamp = From });
        await db.SaveChangesAsync();
        var root = $"/api/projects/{project.Id}/insights/period-comparison?event=signup&";
        var result = (await client.GetFromJsonAsync<PeriodComparison>(root + Bounds(From, To)))!;
        Assert.Equal(2, result.CurrentCount);
        Assert.Equal(2, result.PreviousCount);
        var equivalent = await client.GetFromJsonAsync<PeriodComparison>(root + Bounds(From.ToOffset(TimeSpan.FromHours(2)), To.ToOffset(TimeSpan.FromHours(-5))));
        Assert.Equal(result, equivalent);
        foreach (var bounds in new[] { Bounds(From, From), Bounds(To, From), Bounds(From, From.AddDays(91)), Bounds(DateTimeOffset.MinValue, DateTimeOffset.MinValue.AddDays(1)), "from=bad&to=bad", "" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(root + bounds)).StatusCode);
        using var keyClient = factory.CreateClient();
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", project.ReadKey);
        Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync(root + Bounds(From, To))).StatusCode);
        keyClient.DefaultRequestHeaders.Remove("X-Api-Key");
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", project.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync(root + Bounds(From, To))).StatusCode);
    }

    [Theory]
    [InlineData("hour")]
    [InlineData("day")]
    [InlineData("week")]
    public async Task MultiTrend_MatchesStandaloneBuckets_AndReturnsAnnotationsOnce(string interval)
    {
        var (client, project) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var personId = Guid.NewGuid();
        db.Events.AddRange(Enumerable.Range(0, 3).Select(index => new AnalyticsEvent { ProjectId = project.Id, Name = "signup", DistinctId = "p", PersonId = personId, Timestamp = From.AddHours(index) }));
        db.Events.Add(new AnalyticsEvent { ProjectId = project.Id, Name = "purchase", DistinctId = "p", PersonId = personId, Timestamp = To });
        var annotation = new Annotation { ProjectId = project.Id, Date = DateOnly.FromDateTime(From.UtcDateTime), Content = "Launch" };
        db.Annotations.Add(annotation);
        await db.SaveChangesAsync();
        var names = new[] { "purchase", "missing", "signup" };
        using var response = await client.PostAsJsonAsync($"/api/projects/{project.Id}/insights/multi-trend", new { events = names, from = From, to = To, interval });
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<MultiTrendResult>())!;
        Assert.Equal(names, result.Series.Select(s => s.Event));
        Assert.Equal(annotation.Id, Assert.Single(result.Annotations).Id);
        Assert.Equal(1, result.Series[0].Buckets.Sum(b => b.Count));
        Assert.All(result.Series[1].Buckets, b => Assert.Equal(0, b.Count));
        Assert.Equal(3, result.Series[2].Buckets.Sum(b => b.Count));
        foreach (var series in result.Series)
        {
            var standalone = (await client.GetFromJsonAsync<TrendResult>($"/api/projects/{project.Id}/insights/trend?event={series.Event}&interval={interval}&{Bounds(From, To)}"))!;
            Assert.Equal(standalone.Buckets, series.Buckets);
            Assert.Equal(result.Series[0].Buckets.Select(b => b.Start), series.Buckets.Select(b => b.Start));
        }
    }

    [Fact]
    public async Task MultiTrend_ValidatesWholeRequest_AndRetainsReadKeyBoundary()
    {
        var (client, project) = await SetupAsync();
        var path = $"/api/projects/{project.Id}/insights/multi-trend";
        foreach (var events in new[] { Array.Empty<string>(), new[] { "signup", " signup " }, new[] { "a", "b", "c", "d", " " }, new[] { "a", "b", "c", "d", "e", "f" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { events, from = From, to = To, interval = "day" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { events = new[] { "a" }, from = From, to = To, interval = "0" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { events = new[] { "a" }, interval = "day" })).StatusCode);
        using var keyClient = factory.CreateClient();
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", project.ReadKey);
        var body = new { events = new[] { "a", "A" }, from = From, to = From, interval = "day" };
        Assert.Equal(HttpStatusCode.OK, (await keyClient.PostAsJsonAsync(path, body)).StatusCode);
        keyClient.DefaultRequestHeaders.Remove("X-Api-Key");
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", project.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsJsonAsync(path, body)).StatusCode);
    }
}
