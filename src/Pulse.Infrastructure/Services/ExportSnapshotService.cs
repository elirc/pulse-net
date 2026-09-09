using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public sealed class ExportSnapshotLimitException(string code) : Exception(code) { public string Code { get; } = code; }

public sealed class ExportSnapshotService(PulseDbContext db, ExportOwnershipService ownership, TimeProvider clock)
{
    public const int MaxRows = 10000;
    public const int MaxBytes = 16 * 1024 * 1024;
    public async Task CaptureAsync(ExportAttempt attempt, string? eventName, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (from > to || to - from > TimeSpan.FromDays(90)) throw new InvalidOperationException("invalid_snapshot_range");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ownership.FenceAsync(attempt, ct);
        if (await db.ExportJobs.AnyAsync(j => j.ProjectId == attempt.ProjectId && j.Id == attempt.JobId && j.SnapshotReady, ct)) return;
        var capturedAt = clock.GetUtcNow();
        var source = db.Events.AsNoTracking().Where(e => e.ProjectId == attempt.ProjectId && e.Timestamp >= from && e.Timestamp <= to &&
            (eventName == null || e.Name == eventName)).OrderBy(e => e.Timestamp).ThenBy(e => e.Id).Take(MaxRows + 1);
        var rows = new List<ExportSnapshotRow>(); long bytes = 0;
        await foreach (var item in source.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (rows.Count == MaxRows) throw new ExportSnapshotLimitException("snapshot_row_limit_exceeded");
            var row = new EventExportRow(item.Id, item.Timestamp, item.Name, item.DistinctId, item.PersonId, JsonSerializer.Deserialize<JsonElement>(item.PropertiesJson));
            var json = JsonSerializer.Serialize(row, JsonSerializerOptions.Web);
            bytes += Encoding.UTF8.GetByteCount(json);
            if (bytes > MaxBytes) throw new ExportSnapshotLimitException("snapshot_byte_limit_exceeded");
            rows.Add(new ExportSnapshotRow { JobId = attempt.JobId, ProjectId = attempt.ProjectId, Ordinal = rows.Count, RowJson = json });
        }
        await db.ExportSnapshotRows.Where(r => r.ProjectId == attempt.ProjectId && r.JobId == attempt.JobId).ExecuteDeleteAsync(ct);
        db.ExportSnapshotRows.AddRange(rows);
        await db.SaveChangesAsync(ct);
        var ready = await db.ExportJobs.Where(j => j.ProjectId == attempt.ProjectId && j.Id == attempt.JobId && j.Owner == attempt.Owner &&
            j.AttemptGeneration == attempt.Generation && j.Status == ExportJobStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.SnapshotReady, true).SetProperty(j => j.SnapshotCapturedAt, capturedAt), ct);
        if (ready != 1) throw new LostExportAttemptException();
        await transaction.CommitAsync(ct);
    }

    public async Task<List<EventExportRow>> ReadPageAsync(ExportAttempt attempt, int offset, CancellationToken ct)
    {
        var json = await db.ExportSnapshotRows.AsNoTracking().Where(r => r.ProjectId == attempt.ProjectId && r.JobId == attempt.JobId && r.Ordinal >= offset)
            .OrderBy(r => r.Ordinal).Select(r => r.RowJson).Take(1000).ToListAsync(ct);
        return json.Select(row => JsonSerializer.Deserialize<EventExportRow>(row, JsonSerializerOptions.Web)!).ToList();
    }
}
