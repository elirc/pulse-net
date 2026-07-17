using System.Text.Json;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record DemoSeedResult(
    Guid ProjectId,
    string ProjectName,
    string ApiKey,
    string ReadKey,
    string DemoUserEmail,
    string DemoUserPassword,
    int Users,
    int Events,
    int Persons);

/// <summary>
/// Generates a realistic demo dataset by pushing events through the real
/// capture pipeline (so identity merging, person properties and timestamps
/// behave exactly like production traffic). Deterministic for a given seed.
/// </summary>
public class DemoDataSeeder
{
    private static readonly string[] Pages =
        ["/", "/pricing", "/docs", "/blog", "/features", "/signup"];

    private static readonly string[] Features =
        ["dashboards", "funnels", "retention", "exports", "alerts"];

    private static readonly string[] Referrers =
        ["google", "twitter", "newsletter", "direct", "producthunt"];

    private readonly PulseDbContext _db;
    private readonly CaptureService _capture;
    private readonly TimeProvider _clock;

    public DemoDataSeeder(PulseDbContext db, CaptureService capture, TimeProvider clock)
    {
        _db = db;
        _capture = capture;
        _clock = clock;
    }

    public async Task<DemoSeedResult> SeedAsync(
        int users = 40,
        int days = 30,
        int seed = 20260716,
        CancellationToken ct = default)
    {
        var random = new Random(seed);

        var project = new Project
        {
            Name = "Demo — Hedgehog SaaS",
            ApiKey = ApiKeyGenerator.NewKey(),
            ReadKey = ApiKeyGenerator.NewReadKey(),
        };
        _db.Projects.Add(project);

        // A demo operator account so the management API is usable immediately.
        // The email gets a random suffix so repeated seeds don't collide on
        // the unique email index.
        const string demoPassword = "pulse-demo";
        var demoUser = new User
        {
            Email = $"demo+{Guid.NewGuid():N}@pulse.dev",
            Name = "Demo User",
            PasswordHash = PasswordHasher.Hash(demoPassword),
        };
        _db.Users.Add(demoUser);
        _db.ProjectMemberships.Add(new ProjectMembership
        {
            ProjectId = project.Id,
            UserId = demoUser.Id,
        });
        await _db.SaveChangesAsync(ct);

        var end = _clock.GetUtcNow().Date; // Today at midnight UTC.
        var start = end.AddDays(-(days - 1));
        var totalEvents = 0;

        for (var i = 0; i < users; i++)
        {
            var anonId = $"anon-device-{i:D3}";
            var userId = $"user-{i:D3}@demo.dev";
            var firstDay = random.Next(days);
            var journey = new List<IncomingEvent>();

            var signsUp = random.NextDouble() < 0.6;
            var activates = signsUp && random.NextDouble() < 0.7;
            var purchases = activates && random.NextDouble() < 0.5;
            var identified = false;

            for (var d = firstDay; d < days; d++)
            {
                // Users don't come back every day.
                if (d > firstDay && random.NextDouble() > 0.45)
                {
                    continue;
                }

                var day = start.AddDays(d);
                var distinctId = identified ? userId : anonId;

                foreach (var _ in Enumerable.Range(0, random.Next(1, 4)))
                {
                    journey.Add(Event("pageview", distinctId, At(day, random),
                        Json(new { url = Pages[random.Next(Pages.Length)] })));
                }

                if (signsUp && !identified && d >= firstDay + random.Next(0, 2))
                {
                    var signupAt = At(day, random);
                    journey.Add(Event("signup", anonId, signupAt,
                        Json(new Dictionary<string, object>
                        {
                            ["$set"] = new { email = userId, plan = "free" },
                            ["$set_once"] = new
                            {
                                initial_referrer = Referrers[random.Next(Referrers.Length)],
                            },
                        })));
                    journey.Add(Event("$identify", userId, signupAt.AddSeconds(1),
                        Json(new Dictionary<string, object>
                        {
                            ["$anon_distinct_id"] = anonId,
                        })));
                    identified = true;
                    continue;
                }

                if (activates && identified && journey.All(e => e.Name != "activate"))
                {
                    journey.Add(Event("activate", userId, At(day, random),
                        Json(new { feature = Features[random.Next(Features.Length)] })));
                    continue;
                }

                if (purchases && identified
                    && journey.Any(e => e.Name == "activate")
                    && journey.All(e => e.Name != "purchase"))
                {
                    journey.Add(Event("purchase", userId, At(day, random),
                        Json(new Dictionary<string, object>
                        {
                            ["amount"] = random.Next(19, 199),
                            ["$set"] = new { plan = "pro" },
                        })));
                }
            }

            if (journey.Count == 0)
            {
                continue;
            }

            await _capture.IngestAsync(
                project,
                journey.OrderBy(e => e.Timestamp).ToList(),
                ct);
            totalEvents += journey.Count;
        }

        var persons = _db.Persons.Count(p => p.ProjectId == project.Id);

        return new DemoSeedResult(
            project.Id, project.Name, project.ApiKey, project.ReadKey,
            demoUser.Email, demoPassword, users, totalEvents, persons);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static IncomingEvent Event(
        string name, string distinctId, DateTimeOffset timestamp, string propertiesJson) =>
        new(name, distinctId, timestamp, propertiesJson);

    private static DateTimeOffset At(DateTime day, Random random) =>
        new DateTimeOffset(day, TimeSpan.Zero)
            .AddHours(random.Next(8, 22))
            .AddMinutes(random.Next(60))
            .AddSeconds(random.Next(60));
}
