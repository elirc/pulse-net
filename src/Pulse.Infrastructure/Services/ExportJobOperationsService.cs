using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record ExportJobMetadata(Guid Id, Guid ProjectId, string Type, string Format, ExportJobStatus Status, int RowCount,
    string? Error, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string Consistency,
    DateTimeOffset? SnapshotCapturedAt, long AttemptGeneration, DateTimeOffset? LastHeartbeatAt);
public record ExportHistoryPage(IReadOnlyList<ExportJobMetadata> Jobs, string? NextCursor);
public record ExportRetryOutcome(int Status, ExportJob? Job = null);
public record ExportIntegrity(Guid JobId, string ContentType, long ByteLength, string Sha256, string Encoding);

public static class ExportDocument
{
    public static string StateName(ExportJobStatus state) => state == ExportJobStatus.CancelRequested ? "cancelRequested" : state.ToString().ToLowerInvariant();
    public static byte[] Bytes(string content) => Encoding.UTF8.GetBytes(content);
    public static string ContentType(string? type) => (type ?? "application/json") +
        (type?.Contains("charset=", StringComparison.OrdinalIgnoreCase) == true ? "" : "; charset=utf-8");
    public static ExportIntegrity Integrity(ExportJob job)
    {
        var bytes = Bytes(job.ResultContent!);
        return new(job.Id, ContentType(job.ContentType), bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)), "utf-8");
    }
}

public sealed class ExportJobOperationsService(PulseDbContext db, ExportSignal signal, TimeProvider clock)
{
    public static readonly Expression<Func<ExportJob, ExportJobMetadata>> Metadata = job =>
        new(job.Id, job.ProjectId, job.Type, job.Format, job.Status, job.RowCount, job.Error, job.CreatedAt, job.CompletedAt,
            job.Consistency, job.SnapshotCapturedAt, job.AttemptGeneration, job.LastHeartbeatAt);

    public async Task EnqueueAsync(ExportJob job, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProjectMaintenance.AssertWritableAsync(db, job.ProjectId, ct);
        db.ExportJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        signal.Ring();
    }

    public async Task<ExportRetryOutcome> RetryAsync(Guid projectId, Guid jobId, CancellationToken ct)
    {
        var source = await db.ExportJobs.AsNoTracking().Where(j => j.ProjectId == projectId && j.Id == jobId)
            .Select(j => new { j.Status, j.Type, j.Format, j.ParamsJson, j.Consistency }).SingleOrDefaultAsync(ct);
        if (source is null) return new(404);
        if (source.Status != ExportJobStatus.Failed) return new(409);
        var job = new ExportJob { ProjectId = projectId, Type = source.Type, Format = source.Format,
            ParamsJson = source.ParamsJson, Consistency = source.Consistency, CreatedAt = clock.GetUtcNow() };
        await EnqueueAsync(job, ct);
        return new(202, job);
    }

    public async Task<ExportHistoryPage> HistoryAsync(Guid projectId, ExportJobStatus? status, string? type, int limit,
        string filterContext, ScopedCursor? cursor, CancellationToken ct)
    {
        var query = db.ExportJobs.AsNoTracking().Where(j => j.ProjectId == projectId);
        if (status is not null) query = query.Where(j => j.Status == status);
        if (type is not null) query = query.Where(j => j.Type == type);
        if (cursor is not null)
        {
            var created = new DateTimeOffset(cursor.Ticks, TimeSpan.Zero);
            var id = cursor.Id;
            query = query.Where(j => j.CreatedAt < created || (j.CreatedAt == created && j.Id.CompareTo(id) < 0));
        }
        var jobs = await query.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Select(Metadata).Take(limit + 1).ToListAsync(ct);
        var more = jobs.Count > limit;
        if (more) jobs.RemoveAt(jobs.Count - 1);
        var next = more ? new ScopedCursor(1, "export-history", projectId, null, filterContext, jobs[^1].CreatedAt.UtcTicks, jobs[^1].Id).Encode() : null;
        return new(jobs, next);
    }

    public async Task<int> DeleteAsync(Guid projectId, Guid jobId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var removed = await db.ExportJobs.Where(j => j.ProjectId == projectId && j.Id == jobId &&
            (j.Status == ExportJobStatus.Completed || j.Status == ExportJobStatus.Failed || j.Status == ExportJobStatus.Cancelled)).ExecuteDeleteAsync(ct);
        if (removed == 0) return await db.ExportJobs.AnyAsync(j => j.ProjectId == projectId && j.Id == jobId, ct) ? 409 : 404;
        await db.ExportSnapshotRows.Where(r => r.ProjectId == projectId && r.JobId == jobId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return 204;
    }
}
