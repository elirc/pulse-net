using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static class HourlyAlertEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 100;

    public static IEndpointRouteBuilder MapHourlyAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var rules = app.MapGroup("/api/projects/{projectId:guid}/alert-rules");

        rules.MapGet("/", ListRulesAsync);
        rules.MapGet("/{ruleId:guid}", GetRuleAsync);
        rules.MapPost("/", CreateRuleAsync);
        rules.MapPut("/{ruleId:guid}", UpdateRuleAsync);
        rules.MapDelete("/{ruleId:guid}", DeleteRuleAsync);

        var notifications = app.MapGroup("/api/projects/{projectId:guid}/notifications");
        notifications.MapGet("/", ListNotificationsAsync);
        notifications.MapPut("/{notificationId:guid}/read", MarkReadAsync);
        return app;
    }

    private static async Task<IResult> ListRulesAsync(
        Guid projectId, int? limit, int? offset, HttpContext http,
        PulseDbContext db, ProjectAccessService access, CancellationToken ct)
    {
        if (await access.RequireRoleAsync(http, projectId, ProjectRole.Viewer, ct) is { } denied) return denied;
        var page = Page(limit, offset);
        var items = await db.Set<AlertRule>().AsNoTracking()
            .Where(r => r.ProjectId == projectId && !r.IsDeleted)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Skip(page.Offset).Take(page.Limit)
            .Select(r => ToRuleResponse(r)).ToListAsync(ct);
        return Results.Ok(new AlertRulePage(items, page.Limit, page.Offset));
    }

    private static async Task<IResult> GetRuleAsync(
        Guid projectId, Guid ruleId, HttpContext http,
        PulseDbContext db, ProjectAccessService access, CancellationToken ct)
    {
        if (await access.RequireRoleAsync(http, projectId, ProjectRole.Viewer, ct) is { } denied) return denied;
        var rule = await db.Set<AlertRule>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.ProjectId == projectId && r.Id == ruleId && !r.IsDeleted, ct);
        return rule is null ? Results.NotFound() : Results.Ok(ToRuleResponse(rule));
    }

    private static async Task<IResult> CreateRuleAsync(
        Guid projectId, CreateAlertRuleRequest request, HttpContext http,
        PulseDbContext db, ProjectAccessService access, TimeProvider clock, CancellationToken ct)
    {
        if (await access.RequireRoleAsync(http, projectId, ProjectRole.Editor, ct) is { } denied) return denied;
        if (Validate(request.Name, request.EventName, request.Threshold) is { Count: > 0 } errors)
            return Results.ValidationProblem(errors);

        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProjectMaintenance.AssertWritableAsync(db, projectId, ct);
        if (await db.Set<AlertRule>().CountAsync(r => r.ProjectId == projectId && !r.IsDeleted, ct) >= 20)
            return Results.Problem("A project can have at most 20 active alert rules.", statusCode: StatusCodes.Status409Conflict);

        var rule = new AlertRule
        {
            ProjectId = projectId,
            Name = request.Name.Trim(),
            EventName = request.EventName.Trim(),
            Threshold = request.Threshold,
            Enabled = request.Enabled,
            CreatedAt = now,
            UpdatedAt = now,
            NextWindowStart = HourlyAlertService.FirstEligibleWindowStart(now),
        };
        db.Set<AlertRule>().Add(rule);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/projects/{projectId}/alert-rules/{rule.Id}", ToRuleResponse(rule));
    }

    private static async Task<IResult> UpdateRuleAsync(
        Guid projectId, Guid ruleId, UpdateAlertRuleRequest request, HttpContext http,
        PulseDbContext db, ProjectAccessService access, TimeProvider clock, CancellationToken ct)
    {
        if (await access.RequireRoleAsync(http, projectId, ProjectRole.Editor, ct) is { } denied) return denied;
        if (Validate(request.Name, request.EventName, request.Threshold) is { Count: > 0 } errors)
            return Results.ValidationProblem(errors);
        if (request.Revision is < 1 or int.MaxValue)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["revision"] = ["Revision must be between 1 and 2,147,483,646."] });

        var now = clock.GetUtcNow();
        var next = HourlyAlertService.FirstEligibleWindowStart(now);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProjectMaintenance.AssertWritableAsync(db, projectId, ct);
        var changed = await db.Set<AlertRule>()
            .Where(r => r.ProjectId == projectId && r.Id == ruleId && !r.IsDeleted && r.Revision == request.Revision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Name, request.Name.Trim())
                .SetProperty(r => r.EventName, request.EventName.Trim())
                .SetProperty(r => r.Threshold, request.Threshold)
                .SetProperty(r => r.Enabled, request.Enabled)
                .SetProperty(r => r.Revision, request.Revision + 1)
                .SetProperty(r => r.NextWindowStart, next)
                .SetProperty(r => r.SkippedThrough, (DateTimeOffset?)null)
                .SetProperty(r => r.UpdatedAt, now), ct);
        if (changed == 0)
        {
            await transaction.RollbackAsync(ct);
            return await RuleConflictOrMissingAsync(db, projectId, ruleId, ct);
        }
        await transaction.CommitAsync(ct);
        var updated = await db.Set<AlertRule>().AsNoTracking().SingleAsync(r => r.Id == ruleId, ct);
        return Results.Ok(ToRuleResponse(updated));
    }

    private static async Task<IResult> DeleteRuleAsync(
        Guid projectId, Guid ruleId, int? revision, HttpContext http,
        PulseDbContext db, ProjectAccessService access, TimeProvider clock, CancellationToken ct)
    {
        if (await access.RequireRoleAsync(http, projectId, ProjectRole.Editor, ct) is { } denied) return denied;
        if (revision is null or < 1 or int.MaxValue)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["revision"] = ["Revision must be between 1 and 2,147,483,646."] });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProjectMaintenance.AssertWritableAsync(db, projectId, ct);
        var changed = await db.Set<AlertRule>()
            .Where(r => r.ProjectId == projectId && r.Id == ruleId && !r.IsDeleted && r.Revision == revision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Enabled, false)
                .SetProperty(r => r.IsDeleted, true)
                .SetProperty(r => r.Revision, revision.Value + 1)
                .SetProperty(r => r.UpdatedAt, clock.GetUtcNow()), ct);
        if (changed == 0)
        {
            await transaction.RollbackAsync(ct);
            return await RuleConflictOrMissingAsync(db, projectId, ruleId, ct);
        }
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListNotificationsAsync(
        Guid projectId, int? limit, int? offset, HttpContext http,
        PulseDbContext db, ProjectAccessService access, CancellationToken ct)
    {
        if (await access.RequireRoleAsync(http, projectId, ProjectRole.Viewer, ct) is { } denied) return denied;
        var userId = ProjectAccessService.GetUserId(http.User)!.Value;
        var page = Page(limit, offset);
        var items = await db.Set<ProjectNotification>().AsNoTracking()
            .Where(n => n.ProjectId == projectId)
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Skip(page.Offset).Take(page.Limit)
            .Select(n => new NotificationResponse(
                n.Id, n.RuleId, n.RuleRevision, n.RuleName, n.EventName,
                n.WindowStart, n.WindowEnd, n.ObservedCount, n.Threshold,
                n.EvaluatedAt, n.CreatedAt,
                db.Set<NotificationRead>().Where(r => r.NotificationId == n.Id && r.UserId == userId)
                    .Select(r => (DateTimeOffset?)r.ReadAt).SingleOrDefault()))
            .ToListAsync(ct);
        return Results.Ok(new NotificationPage(items, page.Limit, page.Offset));
    }

    private static async Task<IResult> MarkReadAsync(
        Guid projectId, Guid notificationId, HttpContext http,
        PulseDbContext db, ProjectAccessService access, TimeProvider clock, CancellationToken ct)
    {
        if (await access.RequireRoleAsync(http, projectId, ProjectRole.Viewer, ct) is { } denied) return denied;
        var userId = ProjectAccessService.GetUserId(http.User)!.Value;
        if (!await db.Set<ProjectNotification>().AnyAsync(n => n.ProjectId == projectId && n.Id == notificationId, ct))
            return Results.NotFound();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProjectMaintenance.AssertWritableAsync(db, projectId, ct);
        var readAt = clock.GetUtcNow();
        var readAtUtcTicks = readAt.UtcTicks;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT OR IGNORE INTO NotificationReads (NotificationId, UserId, ReadAt) VALUES ({notificationId}, {userId}, {readAtUtcTicks})", ct);
        var stored = await db.Set<NotificationRead>().AsNoTracking()
            .SingleAsync(r => r.NotificationId == notificationId && r.UserId == userId, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new MarkNotificationReadResponse(notificationId, stored.ReadAt));
    }

    private static Dictionary<string, string[]> Validate(string? name, string? eventName, int threshold)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            errors["name"] = ["Name must contain 1 to 200 characters after trimming."];
        if (string.IsNullOrWhiteSpace(eventName) || eventName.Trim().Length > 200)
            errors["eventName"] = ["Event name must contain 1 to 200 characters after trimming."];
        if (threshold is < 1 or > 1_000_000)
            errors["threshold"] = ["Threshold must be between 1 and 1,000,000."];
        return errors;
    }

    private static async Task<IResult> RuleConflictOrMissingAsync(PulseDbContext db, Guid projectId, Guid ruleId, CancellationToken ct)
    {
        var current = await db.Set<AlertRule>().AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Id == ruleId && !r.IsDeleted)
            .Select(r => (int?)r.Revision).SingleOrDefaultAsync(ct);
        return current is null
            ? Results.NotFound()
            : Results.Problem($"The rule changed. Current revision is {current}.", statusCode: StatusCodes.Status409Conflict);
    }

    private static (int Limit, int Offset) Page(int? limit, int? offset) =>
        (Math.Clamp(limit ?? DefaultPageSize, 1, MaximumPageSize), Math.Max(offset ?? 0, 0));

    private static AlertRuleResponse ToRuleResponse(AlertRule r) => new(
        r.Id, r.Name, r.EventName, r.Threshold, r.Enabled, r.Revision,
        r.NextWindowStart, r.SkippedThrough, r.CreatedAt, r.UpdatedAt);
}

public sealed record CreateAlertRuleRequest(string Name, string EventName, int Threshold, bool Enabled);
public sealed record UpdateAlertRuleRequest(string Name, string EventName, int Threshold, bool Enabled, int Revision);
public sealed record AlertRuleResponse(Guid Id, string Name, string EventName, int Threshold, bool Enabled,
    int Revision, DateTimeOffset NextWindowStart, DateTimeOffset? SkippedThrough,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record AlertRulePage(IReadOnlyList<AlertRuleResponse> Items, int Limit, int Offset);
public sealed record NotificationResponse(Guid Id, Guid RuleId, int RuleRevision, string RuleName,
    string EventName, DateTimeOffset WindowStart, DateTimeOffset WindowEnd, long ObservedCount,
    int Threshold, DateTimeOffset EvaluatedAt, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
public sealed record NotificationPage(IReadOnlyList<NotificationResponse> Items, int Limit, int Offset);
public sealed record MarkNotificationReadResponse(Guid NotificationId, DateTimeOffset ReadAt);
