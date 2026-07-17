using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

/// <summary>One event to ingest, already unwrapped from single/batch shape.</summary>
public record IncomingEvent(
    string Name,
    string DistinctId,
    DateTimeOffset? Timestamp,
    string PropertiesJson);

public record CaptureResult(int Ingested);

/// <summary>
/// Ingestion pipeline: authenticates the project by API key and persists
/// events. All events in a request are written atomically — one bad event
/// rejects the whole payload, so SDKs can safely retry.
/// </summary>
public class CaptureService
{
    private readonly PulseDbContext _db;
    private readonly TimeProvider _clock;

    public CaptureService(PulseDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Project?> FindProjectByApiKeyAsync(string apiKey, CancellationToken ct = default) =>
        await _db.Projects.SingleOrDefaultAsync(p => p.ApiKey == apiKey, ct);

    public async Task<CaptureResult> IngestAsync(
        Project project,
        IReadOnlyList<IncomingEvent> events,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        foreach (var incoming in events)
        {
            _db.Events.Add(new AnalyticsEvent
            {
                ProjectId = project.Id,
                Name = incoming.Name,
                DistinctId = incoming.DistinctId,
                Timestamp = incoming.Timestamp ?? now,
                PropertiesJson = incoming.PropertiesJson,
            });
        }

        await _db.SaveChangesAsync(ct);
        return new CaptureResult(events.Count);
    }
}
