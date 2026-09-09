using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public sealed class HourlyAlertService(PulseDbContext db, TimeProvider clock)
{
    public const int MaximumWindowsPerCycle = 10;
    public static readonly TimeSpan MaximumCatchUp = TimeSpan.FromHours(24);

    public static DateTimeOffset FirstEligibleWindowStart(DateTimeOffset changedAt)
    {
        var utc = changedAt.ToUniversalTime();
        var hour = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        return utc == hour ? hour : hour.AddHours(1);
    }

    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var processed = 0;
        while (processed < MaximumWindowsPerCycle)
        {
            ct.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            var completedThrough = FirstEligibleWindowStart(now);
            if (completedThrough > now)
            {
                completedThrough = completedThrough.AddHours(-1);
            }

            var candidate = await db.Set<AlertRule>()
                .AsNoTracking()
                .Where(r => r.Enabled && !r.IsDeleted && r.NextWindowStart < completedThrough &&
                    !db.ProjectIngestionStates.Any(s => s.ProjectId == r.ProjectId && s.Paused))
                .OrderBy(r => r.NextWindowStart)
                .ThenBy(r => r.Id)
                .Select(r => new { r.Id, r.Revision })
                .FirstOrDefaultAsync(ct);

            if (candidate is null)
            {
                break;
            }

            if (await ProcessNextWindowAsync(candidate.Id, candidate.Revision, completedThrough, now, ct))
            {
                processed++;
            }
            else
            {
                db.ChangeTracker.Clear();
                break;
            }
        }

        return processed;
    }

    private async Task<bool> ProcessNextWindowAsync(
        Guid ruleId,
        int expectedRevision,
        DateTimeOffset completedThrough,
        DateTimeOffset evaluatedAt,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rule = await db.Set<AlertRule>().SingleOrDefaultAsync(
            r => r.Id == ruleId && r.Revision == expectedRevision && r.Enabled && !r.IsDeleted,
            ct);
        if (rule is null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var state = await db.Set<ProjectIngestionState>().AsNoTracking()
            .SingleOrDefaultAsync(s => s.ProjectId == rule.ProjectId, ct);
        if (state?.Paused == true)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var oldestAllowedStart = completedThrough.Subtract(MaximumCatchUp);
        if (rule.NextWindowStart < oldestAllowedStart)
        {
            rule.SkippedThrough = oldestAllowedStart;
            rule.NextWindowStart = oldestAllowedStart;
        }

        var windowStart = rule.NextWindowStart;
        var windowEnd = windowStart.AddHours(1);
        if (windowEnd > completedThrough)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var observedCount = await db.Events.LongCountAsync(e =>
            e.ProjectId == rule.ProjectId &&
            e.Name == rule.EventName &&
            e.Timestamp >= windowStart &&
            e.Timestamp < windowEnd, ct);

        var evaluation = new AlertEvaluation
        {
            RuleId = rule.Id,
            ProjectId = rule.ProjectId,
            RuleRevision = rule.Revision,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            ObservedCount = observedCount,
            Threshold = rule.Threshold,
            Triggered = observedCount >= rule.Threshold,
            EvaluatedAt = evaluatedAt,
        };
        db.Set<AlertEvaluation>().Add(evaluation);

        if (evaluation.Triggered)
        {
            db.Set<ProjectNotification>().Add(new ProjectNotification
            {
                ProjectId = rule.ProjectId,
                EvaluationId = evaluation.Id,
                RuleId = rule.Id,
                RuleRevision = rule.Revision,
                RuleName = rule.Name,
                EventName = rule.EventName,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                ObservedCount = observedCount,
                Threshold = rule.Threshold,
                EvaluatedAt = evaluatedAt,
                CreatedAt = evaluatedAt,
            });
        }

        rule.NextWindowStart = windowEnd;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return true;
    }
}
