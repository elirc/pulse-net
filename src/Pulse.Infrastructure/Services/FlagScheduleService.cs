using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record FlagScheduleOutcome(int Status, FlagRolloutSchedule? Schedule = null);
public sealed class FlagScheduleSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public void Ring() => channel.Writer.TryWrite(true);
    public ValueTask<bool> WaitAsync(CancellationToken ct) => channel.Reader.ReadAsync(ct);
}

public sealed class FlagScheduleService(PulseDbContext db, FlagMutationService mutations, FlagScheduleSignal signal, TimeProvider clock)
{
    public async Task<FlagScheduleOutcome> CreateAsync(Guid projectId, string key, DateTimeOffset executeAt, double percentage,
        long expectedRevision, AuditActor actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (executeAt < now.AddMinutes(1) || executeAt > now.AddDays(30) || !double.IsFinite(percentage) || percentage is < 0 or > 100 || expectedRevision < 1) return new(400);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, p => p.Name), ct);
        var permission = await mutations.CheckEditorAsync(projectId, actor, ct);
        if (permission != 200) return new(permission);
        var flag = await db.FeatureFlags.AsNoTracking().SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct);
        if (flag is null) return new(404);
        if (flag.Revision != expectedRevision) return new(412);
        var prior = await db.FlagRolloutSchedules.AsNoTracking().SingleOrDefaultAsync(s => s.ProjectId == projectId && s.FlagId == flag.Id, ct);
        if (prior?.Status == FlagScheduleStatus.Pending) return new(409);
        if (prior is not null)
        {
            // Delete first inside the transaction to avoid provider insert ordering against the unique FlagId.
            await db.FlagRolloutSchedules.Where(s => s.Id == prior.Id).ExecuteDeleteAsync(ct);
            var tracked = db.FlagRolloutSchedules.Local.FirstOrDefault(s => s.Id == prior.Id);
            if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
        }
        var schedule = new FlagRolloutSchedule { ProjectId = projectId, FlagId = flag.Id, CreatorUserId = actor.UserId, PersonalKeyId = actor.PersonalKeyId,
            CreatedAt = now, ExecuteAt = executeAt.ToUniversalTime(), RolloutPercentage = percentage, ExpectedRevision = expectedRevision };
        db.FlagRolloutSchedules.Add(schedule);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        signal.Ring();
        return new(202, schedule);
    }

    public async Task<int> CancelAsync(Guid projectId, Guid flagId, AuditActor actor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, p => p.Name), ct);
        var permission = await mutations.CheckEditorAsync(projectId, actor, ct);
        if (permission != 200) return permission;
        var now = clock.GetUtcNow();
        var changed = await db.FlagRolloutSchedules.Where(s => s.ProjectId == projectId && s.FlagId == flagId && s.Status == FlagScheduleStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, FlagScheduleStatus.Cancelled).SetProperty(x => x.CompletedAt, now).SetProperty(x => x.Reason, "cancelled_by_editor"), ct);
        if (changed == 0) return await db.FlagRolloutSchedules.AnyAsync(s => s.ProjectId == projectId && s.FlagId == flagId, ct) ? 409 : 404;
        await transaction.CommitAsync(ct);
        return 204;
    }

    public async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.FlagRolloutSchedules.AsNoTracking().Where(s => s.Status == FlagScheduleStatus.Pending && s.ExecuteAt <= now)
            .OrderBy(s => s.ExecuteAt).ThenBy(s => s.Id).Select(s => s.Id).Take(50).ToListAsync(ct);
        var processed = 0;
        foreach (var id in due)
        {
            if (await ProcessOneAsync(id, ct)) processed++;
            db.ChangeTracker.Clear();
        }
        return processed;
    }

    public async Task<bool> ProcessOneAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        // Claim by conditional write before reading: SQLite serializes competing processors/cancellation.
        var claimed = await db.FlagRolloutSchedules.Where(s => s.Id == id && s.Status == FlagScheduleStatus.Pending && s.ExecuteAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Reason, "processing"), ct);
        if (claimed == 0) return false;
        var schedule = await db.FlagRolloutSchedules.SingleAsync(s => s.Id == id, ct);
        if (await db.ProjectIngestionStates.AnyAsync(p => p.ProjectId == schedule.ProjectId && p.Paused, ct)) return false;
        var actor = new AuditActor(schedule.CreatorUserId, schedule.PersonalKeyId);
        var flag = await db.FeatureFlags.AsNoTracking().SingleOrDefaultAsync(f => f.ProjectId == schedule.ProjectId && f.Id == schedule.FlagId, ct);
        var permission = await mutations.CheckEditorAsync(schedule.ProjectId, actor, ct);
        if (permission != 200)
        {
            schedule.Status = FlagScheduleStatus.Blocked;
            schedule.Reason = "creator_no_longer_editor";
        }
        else if (flag is null || flag.Revision != schedule.ExpectedRevision)
        {
            schedule.Status = FlagScheduleStatus.Conflict;
            schedule.Reason = flag is null ? "flag_deleted" : "revision_changed";
        }
        else
        {
            flag.RolloutPercentage = schedule.RolloutPercentage;
            var result = await mutations.UpdateInTransactionAsync(flag, schedule.ExpectedRevision, actor, ct, scheduleId: schedule.Id);
            if (result.Status == 200)
            {
                schedule.Status = FlagScheduleStatus.Applied;
                schedule.AppliedRevision = flag.Revision;
                schedule.Reason = "applied";
            }
            else
            {
                schedule.Status = FlagScheduleStatus.Conflict;
                schedule.Reason = result.Status == 400 ? "configuration_not_supported" : "revision_changed";
            }
        }
        schedule.CompletedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
