using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

/// <summary>Observable contracts for US-07 through US-25, using isolated project fixtures.</summary>
public class JuniorApiStoriesTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private async Task<(HttpClient Client, ProjectResponse Project)> SetupAsync(string name = "Practice")
    {
        var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync("/api/projects", new { name });
        response.EnsureSuccessStatusCode();
        return (client, (await response.Content.ReadFromJsonAsync<ProjectResponse>())!);
    }

    private async Task SeedAsync(Action<PulseDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static string Path(ProjectResponse project, string suffix) => $"/api/projects/{project.Id}/{suffix}";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task ProjectRename_ValidatesTrimmedLength_AndPreservesIdentityAndCredentials()
    {
        var (client, project) = await SetupAsync();
        using (client)
        {
            var bad = await client.PostAsJsonAsync("/api/projects", new { name = new string('x', 201) });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            var valid = await client.PutAsJsonAsync(Path(project, ""), new { name = "  " + new string('x', 200) + "  " });
            valid.EnsureSuccessStatusCode();
            var updated = (await valid.Content.ReadFromJsonAsync<ProjectResponse>())!;
            Assert.Equal(200, updated.Name.Length);
            Assert.Equal(project.Id, updated.Id);
            Assert.Equal(project.ApiKey, updated.ApiKey);
            Assert.Equal(project.ReadKey, updated.ReadKey);
            Assert.Equal(project.CreatedAt, updated.CreatedAt);
            foreach (var name in new[] { " ", new string('x', 201) })
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Path(project, ""), new { name })).StatusCode);
            Assert.Equal(updated, await client.GetFromJsonAsync<ProjectResponse>(Path(project, "")));
        }
    }

    [Fact]
    public async Task ProjectFilters_PrecedePaging_AndOrderingIsDeterministic()
    {
        var (client, first) = await SetupAsync("needle first");
        using (client)
        {
            var second = await ReadAsync(await client.PostAsJsonAsync("/api/projects", new { name = "needle second" }));
            var secondId = second.GetProperty("id").GetGuid();
            await client.PostAsJsonAsync("/api/projects", new { name = "other" });
            var at = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
            await SeedAsync(db => { db.Projects.Single(p => p.Id == first.Id).CreatedAt = at; db.Projects.Single(p => p.Id == secondId).CreatedAt = at.AddHours(1); });
            var page = await client.GetFromJsonAsync<List<ProjectResponse>>("/api/projects?nameContains=needle&sort=NEWEST&limit=1&offset=1");
            Assert.Equal(first.Id, Assert.Single(page!).Id);
            Assert.Empty((await client.GetFromJsonAsync<List<ProjectResponse>>("/api/projects?nameContains=NEEDLE"))!);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/projects?sort=random")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/projects?nameContains=" + new string('x', 201))).StatusCode);
        }
    }

    [Fact]
    public async Task Members_FilterByNormalizedEmail_WithoutFindingUnrelatedAccounts()
    {
        var (client, project) = await SetupAsync();
        using (client)
        using (var member = factory.CreateClient())
        using (var outsider = factory.CreateClient())
        {
            var email = $"needle-{Guid.NewGuid():N}@example.test";
            var registration = await member.PostAsJsonAsync("/api/auth/register", new { email, password = "practice-password-123", name = "Member" });
            registration.EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync(Path(project, "members"), new { email })).EnsureSuccessStatusCode();
            await TestAuth.AuthenticateAsync(outsider);
            var result = await ReadAsync(await client.GetAsync(Path(project, "members?emailContains=NEEDLE-&limit=1")));
            Assert.Equal(email, Assert.Single(result.EnumerateArray()).GetProperty("email").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(Path(project, "members?emailContains=needle"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path(project, "members?emailContains=" + new string('x', 321)))).StatusCode);
        }
    }

    [Fact]
    public async Task PersonQueries_UsePersonCreationAndCanonicalIdentity_WithoutAliasDuplicates()
    {
        var (client, project) = await SetupAsync();
        using (client)
        {
            var at = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
            var a = new Person { ProjectId = project.Id, CreatedAt = at };
            var b = new Person { ProjectId = project.Id, CreatedAt = at.AddDays(1) };
            var foreign = new Person { ProjectId = Guid.NewGuid(), CreatedAt = at };
            await SeedAsync(db =>
            {
                db.Persons.AddRange(a, b, foreign);
                db.PersonDistinctIds.AddRange(
                    new PersonDistinctId { ProjectId = project.Id, PersonId = a.Id, DistinctId = "device-A" },
                    new PersonDistinctId { ProjectId = project.Id, PersonId = a.Id, DistinctId = "account-A" },
                    new PersonDistinctId { ProjectId = foreign.ProjectId, PersonId = foreign.Id, DistinctId = "device-A" });
            });
            var people = await client.GetFromJsonAsync<List<PersonResponse>>(Path(project, "persons?distinctId=device-A"));
            Assert.Equal(a.Id, Assert.Single(people!).Id);
            Assert.Equal(2, people![0].DistinctIds.Count);
            var from = Uri.EscapeDataString(at.ToOffset(TimeSpan.FromHours(2)).ToString("O"));
            var end = Uri.EscapeDataString(at.AddDays(1).ToString("O"));
            var ranged = await client.GetFromJsonAsync<List<PersonResponse>>(Path(project, $"persons?createdFrom={from}&createdBefore={end}"));
            Assert.Equal(a.Id, Assert.Single(ranged!).Id);
            Assert.Equal(2, (await client.GetFromJsonAsync<PersonCountResponse>(Path(project, "persons/count")))!.Count);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path(project, $"persons?createdFrom={from}&createdBefore={from}"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path(project, "persons?distinctId=" + new string('x', 401)))).StatusCode);
        }
    }

    [Fact]
    public async Task PersonCount_CountsProcessedPeople_NotEventsOrManagementUsers()
    {
        var (client, project) = await SetupAsync();
        using (client)
        {
            Assert.Equal(0, (await client.GetFromJsonAsync<PersonCountResponse>(Path(project, "persons/count")))!.Count);
            var result = await client.PostAsJsonAsync("/capture", new { api_key = project.ApiKey, batch = new[] {
                new { @event = "seen", distinct_id = "A" }, new { @event = "seen", distinct_id = "A" }, new { @event = "seen", distinct_id = "B" } } });
            result.EnsureSuccessStatusCode();
            await TestIngestion.WaitForDrainAsync(client);
            Assert.Equal(2, (await client.GetFromJsonAsync<PersonCountResponse>(Path(project, "persons/count")))!.Count);
        }
    }

    [Fact]
    public async Task Annotations_ValidateBeforeMutation_AndCombineTextAndInclusiveDates()
    {
        var (client, project) = await SetupAsync();
        using (client)
        {
            var saved = await ReadAsync(await client.PostAsJsonAsync(Path(project, "annotations"), new { date = "2026-03-01", content = "Release % ready" }));
            var id = saved.GetProperty("id").GetGuid();
            var invalid = await client.PutAsJsonAsync(Path(project, $"annotations/{id}"), new { date = "2026-03-02", content = new string('x', 2001) });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var page = await ReadAsync(await client.GetAsync(Path(project, "annotations?from=2026-03-01&to=2026-03-01&contentContains=%25")));
            Assert.Equal(id, Assert.Single(page.EnumerateArray()).GetProperty("id").GetGuid());
            Assert.Empty((await ReadAsync(await client.GetAsync(Path(project, "annotations?contentContains=release")))).EnumerateArray());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path(project, "annotations?from=2026-03-02&to=2026-03-01"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path(project, "annotations"), new { date = "2026-03-01", content = new string('x', 2001) })).StatusCode);
            (await client.PutAsJsonAsync(Path(project, $"annotations/{id}"), new { content = "  " + new string('x', 2000) + " " })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Definitions_FilterLiteralPrefixesAndStoredTypes()
    {
        var (client, project) = await SetupAsync();
        using (client)
        {
            await SeedAsync(db =>
            {
                db.EventDefinitions.AddRange(new EventDefinition { ProjectId = project.Id, Name = "Signup%done" }, new EventDefinition { ProjectId = project.Id, Name = "Signup-other" });
                db.PropertyDefinitions.AddRange(new PropertyDefinition { ProjectId = project.Id, Name = "plan", PropertyType = "string" }, new PropertyDefinition { ProjectId = project.Id, Name = "amount", PropertyType = "number" });
            });
            var events = await ReadAsync(await client.GetAsync(Path(project, "event-definitions?namePrefix=signup%25")));
            Assert.Equal("Signup%done", Assert.Single(events.EnumerateArray()).GetProperty("name").GetString());
            var properties = await ReadAsync(await client.GetAsync(Path(project, "property-definitions?type=NUMBER")));
            Assert.Equal("amount", Assert.Single(properties.EnumerateArray()).GetProperty("name").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path(project, "property-definitions?type=integer"))).StatusCode);
        }
    }

    [Fact]
    public async Task FlagFilters_ReferToActiveConfiguration_NotDecisionOutcome()
    {
        var (client, project) = await SetupAsync();
        using (client)
        {
            foreach (var item in new[] { ("Feature_on", true), ("Feature_off", false), ("other", true) })
                (await client.PostAsJsonAsync(Path(project, "feature-flags"), new { key = item.Item1, name = item.Item1, type = "boolean", active = item.Item2, rolloutPercentage = 0 })).EnsureSuccessStatusCode();
            var result = await ReadAsync(await client.GetAsync(Path(project, "feature-flags?keyPrefix=feature_&active=true")));
            Assert.Equal("Feature_on", Assert.Single(result.EnumerateArray()).GetProperty("key").GetString());
            Assert.Equal(0, result[0].GetProperty("rolloutPercentage").GetDouble());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path(project, "feature-flags?active=perhaps"))).StatusCode);
        }
    }

    [Fact]
    public async Task DeadLetters_FilterBeforePaging_AndDetailIsScopedAndReadOnly()
    {
        var (client, project) = await SetupAsync();
        using (client)
        {
            var at = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
            var old = new DeadLetterEvent { ProjectId = project.Id, PayloadJson = "{}", Error = "retry % failed", FailedAt = at };
            var recent = new DeadLetterEvent { ProjectId = project.Id, PayloadJson = "{}", Error = "retry % failed", FailedAt = at.AddHours(1) };
            var foreign = new DeadLetterEvent { ProjectId = Guid.NewGuid(), PayloadJson = "{}", Error = "retry % failed", FailedAt = at };
            await SeedAsync(db => db.DeadLetterEvents.AddRange(old, recent, foreign, new DeadLetterEvent { ProjectId = project.Id, PayloadJson = "retry % failed", Error = "other", FailedAt = at.AddHours(2) }));
            var page = await client.GetFromJsonAsync<List<DeadLetterResponse>>(Path(project, "ingestion/dead-letters?errorContains=%25&limit=1&offset=1"));
            Assert.Equal(old.Id, Assert.Single(page!).Id);
            var detail = await client.GetFromJsonAsync<DeadLetterResponse>(Path(project, $"ingestion/dead-letters/{old.Id}"));
            Assert.Equal(old.Error, detail!.Error);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path(project, $"ingestion/dead-letters/{foreign.Id}"))).StatusCode);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            Assert.Equal(3, await db.DeadLetterEvents.CountAsync(d => d.ProjectId == project.Id));
            Assert.False(await db.QueuedEvents.AnyAsync(q => q.ProjectId == project.Id));
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task Export_ReversedRangeIsProblemJson_EqualBoundsRemainValid(string format)
    {
        var (client, project) = await SetupAsync();
        using (client)
        {
            var invalid = await client.GetAsync(Path(project, $"export/events?format={format}&from=2026-03-02T00:00:00Z&to=2026-03-01T00:00:00Z"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Path(project, $"export/events?format={format}&from=2026-03-01T00:00:00Z&to=2026-03-01T00:00:00Z"))).StatusCode);
        }
    }

    [Theory]
    [InlineData("persons/count")]
    [InlineData("persons?distinctId=A")]
    [InlineData("annotations?contentContains=x")]
    [InlineData("event-definitions?namePrefix=x")]
    [InlineData("property-definitions?type=string")]
    [InlineData("feature-flags?active=true")]
    [InlineData("ingestion/dead-letters?errorContains=x")]
    public async Task ManagementQueries_RejectAnonymousProjectKeysAndNonmembers(string suffix)
    {
        var (owner, project) = await SetupAsync();
        using (owner)
        using (var caller = factory.CreateClient())
        {
            var path = Path(project, suffix);
            foreach (var key in new[] { "", project.ApiKey, project.ReadKey })
            {
                caller.DefaultRequestHeaders.Remove("X-Api-Key");
                if (key.Length > 0) caller.DefaultRequestHeaders.Add("X-Api-Key", key);
                Assert.Equal(HttpStatusCode.Unauthorized, (await caller.GetAsync(path)).StatusCode);
            }
            caller.DefaultRequestHeaders.Remove("X-Api-Key");
            await TestAuth.AuthenticateAsync(caller);
            Assert.Equal(HttpStatusCode.NotFound, (await caller.GetAsync(path)).StatusCode);
        }
    }
}
