using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class ExportTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public ExportTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // --- Synchronous event export ------------------------------------------------

    [Fact]
    public async Task ExportEvents_Json_PaginatesWithCursor()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-01T10:00:00Z"),
            Ev("pageview", "u2", "2026-03-01T11:00:00Z"),
            Ev("pageview", "u3", "2026-03-01T12:00:00Z"));

        var page1 = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/export/events?limit=2");
        var events1 = page1.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(2, events1.Count);
        var cursor = page1.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var page2 = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/export/events?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        var events2 = page2.GetProperty("events").EnumerateArray().ToList();
        Assert.Single(events2);
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);

        // No overlap between pages; ordered by timestamp.
        var ids1 = events1.Select(e => e.GetProperty("id").GetGuid()).ToHashSet();
        Assert.DoesNotContain(events2[0].GetProperty("id").GetGuid(), ids1);
        Assert.Equal("u3", events2[0].GetProperty("distinctId").GetString());
    }

    [Fact]
    public async Task ExportEvents_RespectsEventAndPropertyFilters()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            EvProps("pageview", "u1", "2026-03-01T10:00:00Z", new { url = "/pricing" }),
            EvProps("pageview", "u2", "2026-03-01T11:00:00Z", new { url = "/docs" }),
            Ev("signup", "u1", "2026-03-01T12:00:00Z"));

        var filters = """[{"property":"url","operator":"equals","value":"/pricing"}]""";
        var page = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/export/events?event=pageview" +
            $"&filters={Uri.EscapeDataString(filters)}");

        var events = page.GetProperty("events").EnumerateArray().ToList();
        var row = Assert.Single(events);
        Assert.Equal("u1", row.GetProperty("distinctId").GetString());
        Assert.Equal("/pricing", row.GetProperty("properties").GetProperty("url").GetString());
    }

    [Fact]
    public async Task ExportEvents_Csv_QuotesAndEscapes()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            EvProps("comma, event", "u\"quote", "2026-03-01T10:00:00Z", new { note = "hello" }));

        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/export/events?format=csv");
        response.EnsureSuccessStatusCode();

        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("id,timestamp,event,distinct_id,person_id,properties", lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"comma, event\"", lines[1]);
        Assert.Contains("\"u\"\"quote\"", lines[1]);
    }

    [Fact]
    public async Task ExportEvents_InvalidCursor_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/export/events?cursor=%21%21not-base64%21%21");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Persons export -----------------------------------------------------------

    [Fact]
    public async Task ExportPersons_ReturnsDistinctIdsAndProperties()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            EvSet("signup", "ada", new { plan = "pro" }),
            Ev("visit", "bob", "2026-03-01T10:00:00Z"));

        var page = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/export/persons");

        var persons = page.GetProperty("persons").EnumerateArray().ToList();
        Assert.Equal(2, persons.Count);
        var ada = persons.Single(p =>
            p.GetProperty("distinctIds").EnumerateArray().Any(d => d.GetString() == "ada"));
        Assert.Equal("pro", ada.GetProperty("properties").GetProperty("plan").GetString());
    }

    // --- Query result export --------------------------------------------------------

    [Fact]
    public async Task ExportInsight_Csv_RendersTrendRows()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("signup", "u1", "2026-03-01T10:00:00Z"),
            Ev("signup", "u2", "2026-03-02T10:00:00Z"));

        var insight = await PostAsync<InsightResponse>(
            $"/api/projects/{projectId}/insights",
            new
            {
                name = "Signups",
                type = "trend",
                config = new
                {
                    @event = "signup",
                    from = "2026-03-01T00:00:00Z",
                    to = "2026-03-02T23:59:59Z",
                    interval = "day",
                },
            });

        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/export/insights/{insight.Id}?format=csv");
        response.EnsureSuccessStatusCode();

        var lines = (await response.Content.ReadAsStringAsync()).TrimEnd('\n').Split('\n');
        Assert.Equal("bucket_start,count,unique_persons", lines[0]);
        Assert.Equal(3, lines.Length); // header + 2 day buckets
        Assert.EndsWith(",1,1", lines[1]);
        Assert.EndsWith(",1,1", lines[2]);
    }

    // --- Async export jobs ------------------------------------------------------------

    [Fact]
    public async Task ExportJob_RunsInBackground_AndDownloads()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-01T10:00:00Z"),
            Ev("pageview", "u2", "2026-03-01T11:00:00Z"),
            Ev("other", "u3", "2026-03-01T12:00:00Z"));

        var createResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/exports",
            new { type = "events", format = "csv", @event = "pageview" });
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        var job = (await createResponse.Content.ReadFromJsonAsync<ExportJobResponse>())!;
        Assert.Equal("pending", job.Status);

        var completed = await WaitForJobAsync(projectId, job.Id);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(2, completed.RowCount);

        var download = await _client.GetAsync(
            $"/api/projects/{projectId}/exports/{job.Id}/download");
        download.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", download.Content.Headers.ContentType?.MediaType);
        var lines = (await download.Content.ReadAsStringAsync()).TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length); // header + 2 pageviews
    }

    [Fact]
    public async Task ExportJob_ForInsight_ProducesJson()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey, Ev("signup", "u1", "2026-03-01T10:00:00Z"));

        var insight = await PostAsync<InsightResponse>(
            $"/api/projects/{projectId}/insights",
            new
            {
                name = "Signups",
                type = "trend",
                config = new
                {
                    @event = "signup",
                    from = "2026-03-01T00:00:00Z",
                    to = "2026-03-01T23:59:59Z",
                },
            });

        var create = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/exports",
            new { type = "insight", format = "json", insightId = insight.Id });
        var job = (await create.Content.ReadFromJsonAsync<ExportJobResponse>())!;

        var completed = await WaitForJobAsync(projectId, job.Id);
        Assert.Equal("completed", completed.Status);

        var download = await _client.GetAsync(
            $"/api/projects/{projectId}/exports/{job.Id}/download");
        var result = await download.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("buckets").EnumerateArray()
            .Sum(b => b.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task ExportJob_WithMissingInsight_Fails()
    {
        var (projectId, _) = await CreateProjectAsync();

        var create = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/exports",
            new { type = "insight", format = "csv", insightId = Guid.NewGuid() });
        var job = (await create.Content.ReadFromJsonAsync<ExportJobResponse>())!;

        var finished = await WaitForJobAsync(projectId, job.Id);
        Assert.Equal("failed", finished.Status);
        Assert.Contains("No such insight", finished.Error);

        var download = await _client.GetAsync(
            $"/api/projects/{projectId}/exports/{job.Id}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
    }

    [Fact]
    public async Task ExportJob_InvalidType_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/exports",
            new { type = "everything", format = "csv" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Exports_AreInvisibleToNonMembers()
    {
        var (projectId, _) = await CreateProjectAsync();

        var intruder = _factory.CreateClient();
        await TestAuth.AuthenticateAsync(intruder);

        Assert.Equal(HttpStatusCode.NotFound,
            (await intruder.GetAsync($"/api/projects/{projectId}/export/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await intruder.PostAsJsonAsync(
                $"/api/projects/{projectId}/exports",
                new { type = "events", format = "csv" })).StatusCode);
    }

    // --- Helpers --------------------------------------------------------------------

    private async Task<ExportJobResponse> WaitForJobAsync(Guid projectId, Guid jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await GetAsync<ExportJobResponse>(
                $"/api/projects/{projectId}/exports/{jobId}");
            if (job.Status is "completed" or "failed")
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Export job did not finish in time.");
    }

    private static object Ev(string name, string distinctId, string timestamp) =>
        new { @event = name, distinct_id = distinctId, timestamp };

    private static object EvProps(string name, string distinctId, string timestamp, object properties) =>
        new { @event = name, distinct_id = distinctId, timestamp, properties };

    private static object EvSet(string name, string distinctId, object set) =>
        new
        {
            @event = name,
            distinct_id = distinctId,
            properties = new Dictionary<string, object> { ["$set"] = set },
        };

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Export {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
        return (project.Id, project.ApiKey);
    }

    private async Task CaptureBatchAsync(string apiKey, params object[] events)
    {
        var response = await _client.PostAsJsonAsync(
            "/capture", new { api_key = apiKey, batch = events });
        response.EnsureSuccessStatusCode();
        await TestIngestion.WaitForDrainAsync(_client);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(string url, object payload)
    {
        var response = await _client.PostAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
