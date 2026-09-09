using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Api.Endpoints;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

public sealed class HourlyAlertApiTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    [Fact]
    public async Task Editor_crud_is_revision_conditional_and_lists_are_bounded()
    {
        var (client, project) = await CreateProjectAsync("Alert CRUD");
        var root = $"/api/projects/{project.Id}/alert-rules";
        var created = await CreateRuleAsync(client, root, " Signups ");
        Assert.Equal("Signups", created.Name);
        Assert.Equal(1, created.Revision);

        using var stale = await client.PutAsJsonAsync($"{root}/{created.Id}", new
        {
            name = "Changed", eventName = "signup", threshold = 2, enabled = true, revision = 99,
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var changed = await client.PutAsJsonAsync($"{root}/{created.Id}", new
        {
            name = "Changed", eventName = "signup", threshold = 2, enabled = false, revision = 1,
        });
        changed.EnsureSuccessStatusCode();
        var updated = (await changed.Content.ReadFromJsonAsync<AlertRuleResponse>())!;
        Assert.Equal(2, updated.Revision);
        Assert.False(updated.Enabled);

        Guid evaluationId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var evaluation = new AlertEvaluation
            {
                RuleId = updated.Id, ProjectId = project.Id, RuleRevision = updated.Revision,
                WindowStart = DateTimeOffset.Parse("2026-09-08T11:00:00Z"),
                WindowEnd = DateTimeOffset.Parse("2026-09-08T12:00:00Z"),
                ObservedCount = 2, Threshold = 2, Triggered = true, EvaluatedAt = DateTimeOffset.UtcNow,
            };
            evaluationId = evaluation.Id;
            db.Add(evaluation);
            db.Add(new ProjectNotification
            {
                ProjectId = project.Id, EvaluationId = evaluation.Id, RuleId = updated.Id,
                RuleRevision = updated.Revision, RuleName = updated.Name, EventName = updated.EventName,
                WindowStart = evaluation.WindowStart, WindowEnd = evaluation.WindowEnd,
                ObservedCount = evaluation.ObservedCount, Threshold = evaluation.Threshold,
                EvaluatedAt = evaluation.EvaluatedAt, CreatedAt = evaluation.EvaluatedAt,
            });
            await db.SaveChangesAsync();
        }

        var page = await client.GetFromJsonAsync<AlertRulePage>(root + "?limit=10000&offset=-4");
        Assert.Equal(100, page!.Limit);
        Assert.Equal(0, page.Offset);

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.DeleteAsync($"{root}/{created.Id}?revision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"{root}/{created.Id}?revision=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"{root}/{created.Id}")).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            Assert.True(await db.AlertEvaluations.AnyAsync(e => e.Id == evaluationId));
            Assert.True(await db.ProjectNotifications.AnyAsync(n => n.EvaluationId == evaluationId));
        }
    }

    [Fact]
    public async Task Rule_input_reports_named_validation_boundaries()
    {
        var (client, project) = await CreateProjectAsync("Alert validation");
        var root = $"/api/projects/{project.Id}/alert-rules";
        var invalid = new object[]
        {
            new { name = " ", eventName = "signup", threshold = 1, enabled = true },
            new { name = new string('n', 201), eventName = "signup", threshold = 1, enabled = true },
            new { name = "Rule", eventName = " ", threshold = 1, enabled = true },
            new { name = "Rule", eventName = new string('e', 201), threshold = 1, enabled = true },
            new { name = "Rule", eventName = "signup", threshold = 0, enabled = true },
            new { name = "Rule", eventName = "signup", threshold = 1_000_001, enabled = true },
        };

        foreach (var body in invalid)
        {
            using var response = await client.PostAsJsonAsync(root, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.True(problem.GetProperty("errors").EnumerateObject().Any(), await response.Content.ReadAsStringAsync());
        }

        var created = await CreateRuleAsync(client, root, "Valid");
        using var revisionOverflow = await client.PutAsJsonAsync($"{root}/{created.Id}", new
        {
            name = "Valid", eventName = "signup", threshold = 1, enabled = true, revision = int.MaxValue,
        });
        Assert.Equal(HttpStatusCode.BadRequest, revisionOverflow.StatusCode);
        var revisionProblem = await revisionOverflow.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(revisionProblem.GetProperty("errors").TryGetProperty("revision", out _));
    }

    [Fact]
    public async Task Project_rejects_twenty_first_nondeleted_rule()
    {
        var (client, project) = await CreateProjectAsync("Alert cap");
        var root = $"/api/projects/{project.Id}/alert-rules";
        for (var index = 0; index < 20; index++)
        {
            await CreateRuleAsync(client, root, "Rule " + index);
        }

        using var overflow = await client.PostAsJsonAsync(root, new
        {
            name = "Rule 21", eventName = "signup", threshold = 1, enabled = true,
        });
        Assert.Equal(HttpStatusCode.Conflict, overflow.StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_but_cannot_manage_rules()
    {
        var (admin, project) = await CreateProjectAsync("Alert roles");
        var root = $"/api/projects/{project.Id}/alert-rules";
        await CreateRuleAsync(admin, root, "Visible");

        var viewer = factory.CreateClient();
        var (token, email) = await TestAuth.RegisterAsync(viewer);
        viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            db.ProjectMemberships.Add(new ProjectMembership
            {
                ProjectId = project.Id,
                UserId = userId,
                Role = ProjectRole.Viewer,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(root)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(root, new
        {
            name = "Forbidden", eventName = "signup", threshold = 1, enabled = true,
        })).StatusCode);
    }

    [Fact]
    public async Task Read_state_is_independent_idempotent_and_membership_bound()
    {
        var (first, project) = await CreateProjectAsync("Alert inbox");
        var second = factory.CreateClient();
        var (token, email) = await TestAuth.RegisterAsync(second);
        second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Guid secondUserId;
        Guid notificationId;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            secondUserId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            db.ProjectMemberships.Add(new ProjectMembership
            {
                ProjectId = project.Id,
                UserId = secondUserId,
                Role = ProjectRole.Viewer,
            });
            var rule = new AlertRule
            {
                ProjectId = project.Id, Name = "Inbox", EventName = "signup", Threshold = 1,
                Enabled = true, NextWindowStart = DateTimeOffset.Parse("2026-09-08T12:00:00Z"),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            var evaluation = new AlertEvaluation
            {
                RuleId = rule.Id, ProjectId = project.Id, RuleRevision = 1,
                WindowStart = DateTimeOffset.Parse("2026-09-08T11:00:00Z"),
                WindowEnd = DateTimeOffset.Parse("2026-09-08T12:00:00Z"), ObservedCount = 1,
                Threshold = 1, Triggered = true, EvaluatedAt = DateTimeOffset.UtcNow,
            };
            var notification = new ProjectNotification
            {
                ProjectId = project.Id, EvaluationId = evaluation.Id, RuleId = rule.Id,
                RuleRevision = 1, RuleName = rule.Name, EventName = rule.EventName,
                WindowStart = evaluation.WindowStart, WindowEnd = evaluation.WindowEnd,
                ObservedCount = 1, Threshold = 1, EvaluatedAt = evaluation.EvaluatedAt,
                CreatedAt = evaluation.EvaluatedAt,
            };
            notificationId = notification.Id;
            db.AddRange(rule, evaluation, notification);
            await db.SaveChangesAsync();
        }

        var readPath = $"/api/projects/{project.Id}/notifications/{notificationId}/read";
        (await first.PutAsync(readPath, content: null)).EnsureSuccessStatusCode();
        (await first.PutAsync(readPath, content: null)).EnsureSuccessStatusCode();
        var firstInbox = await first.GetFromJsonAsync<NotificationPage>($"/api/projects/{project.Id}/notifications");
        var secondInbox = await second.GetFromJsonAsync<NotificationPage>($"/api/projects/{project.Id}/notifications");
        Assert.NotNull(firstInbox!.Items.Single().ReadAt);
        Assert.Null(secondInbox!.Items.Single().ReadAt);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            await db.ProjectMemberships.Where(m => m.ProjectId == project.Id && m.UserId == secondUserId).ExecuteDeleteAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound,
            (await second.GetAsync($"/api/projects/{project.Id}/notifications")).StatusCode);
    }

    private async Task<(HttpClient Client, ProjectResponse Project)> CreateProjectAsync(string name)
    {
        var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        using var response = await client.PostAsJsonAsync("/api/projects", new { name });
        response.EnsureSuccessStatusCode();
        return (client, (await response.Content.ReadFromJsonAsync<ProjectResponse>())!);
    }

    private static async Task<AlertRuleResponse> CreateRuleAsync(HttpClient client, string root, string name)
    {
        using var response = await client.PostAsJsonAsync(root, new
        {
            name, eventName = "signup", threshold = 1, enabled = true,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AlertRuleResponse>())!;
    }
}
