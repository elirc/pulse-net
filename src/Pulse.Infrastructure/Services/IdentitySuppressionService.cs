using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Services;

public sealed class MissingSuppressionKeyException(int version) : Exception($"Required suppression key version {version} is unavailable.");
public sealed class SuppressedIdentityException() : Exception("A known identity is suppressed in this project.");
public sealed class ProjectMaintenanceException() : Exception("Project data is temporarily unavailable during maintenance.");

/// <summary>Deployment-owned dedicated HMAC keys. No default secret and no plaintext identity storage.</summary>
public sealed class SuppressionKeyRing
{
    private readonly IReadOnlyDictionary<int, byte[]> keys;
    public int CurrentVersion { get; }
    public static SuppressionKeyRing Unconfigured { get; } = new(1, new Dictionary<int, byte[]>());
    public SuppressionKeyRing(int currentVersion, IReadOnlyDictionary<int, byte[]> keys)
    {
        if (currentVersion < 1 || keys.Any(k => k.Key < 1 || k.Value.Length < 32)) throw new ArgumentException("Suppression keys need positive versions and at least 32 bytes.");
        CurrentVersion = currentVersion; this.keys = keys.ToDictionary(k => k.Key, k => k.Value.ToArray());
    }
    public void Require(int version) { if (!keys.ContainsKey(version)) throw new MissingSuppressionKeyException(version); }
    public string Fingerprint(Guid projectId, string identity, int version)
    {
        Require(version);
        var message = Encoding.UTF8.GetBytes(projectId.ToString("D") + "\n" + identity.Trim());
        return Convert.ToHexStringLower(HMACSHA256.HashData(keys[version], message));
    }
    public async Task VerifyRequiredVersionsAsync(PulseDbContext db, CancellationToken ct)
    {
        var versions = await db.IdentitySuppressions.Select(s => s.KeyVersion).Distinct().ToListAsync(ct);
        foreach (var version in versions) Require(version);
    }
}

public sealed class IdentitySuppressionService(PulseDbContext db, SuppressionKeyRing keys)
{
    public async Task<bool> MatchesAnyAsync(Guid projectId, IReadOnlyList<IncomingEvent> events, CancellationToken ct)
    {
        var versions = await db.IdentitySuppressions.Where(s => s.ProjectId == projectId).Select(s => s.KeyVersion).Distinct().ToListAsync(ct);
        if (versions.Count == 0) return false;
        var identities = events.SelectMany(Identities).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var version in versions)
        {
            var fingerprints = identities.Select(id => keys.Fingerprint(projectId, id, version)).ToArray();
            if (await db.IdentitySuppressions.AnyAsync(s => s.ProjectId == projectId && s.KeyVersion == version && fingerprints.Contains(s.Fingerprint), ct)) return true;
        }
        return false;
    }

    public static IReadOnlyList<string> Identities(IncomingEvent incoming)
    {
        var identities = new List<string> { incoming.DistinctId.Trim() };
        if (incoming.Name.Trim() == CaptureService.IdentifyEventName)
        {
            using var properties = JsonDocument.Parse(incoming.PropertiesJson);
            if (properties.RootElement.TryGetProperty(CaptureService.AnonDistinctIdProperty, out var alias) && alias.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alias.GetString()))
                identities.Add(alias.GetString()!.Trim());
        }
        return identities;
    }

    public static bool TryClassify(string payload, out IncomingEvent? incoming)
    {
        incoming = null;
        if (!ReplayEnvelopeValidator.Validate(payload).Replayable) return false;
        try
        {
            using var envelope = JsonDocument.Parse(payload);
            if (!CaptureFingerprint.HasUniqueObjectMembers(envelope.RootElement)) return false;
            incoming = JsonSerializer.Deserialize<IncomingEvent>(payload)!;
            using var properties = JsonDocument.Parse(incoming.PropertiesJson);
            return CaptureFingerprint.HasUniqueObjectMembers(properties.RootElement);
        }
        catch (JsonException) { return false; }
    }

    public static string ContentHash(string payload) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}

public static class ProjectMaintenance
{
    public static async Task AssertWritableAsync(PulseDbContext db, Guid projectId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Maintenance write checks require a transaction.");
        var state = await db.ProjectIngestionStates.AsNoTracking().Where(s => s.ProjectId == projectId)
            .Select(s => new { s.Paused, s.MaintenanceGeneration }).SingleOrDefaultAsync(ct);
        if (state?.Paused == true || (db.GuardedProjectId == projectId &&
            db.GuardedMaintenanceGeneration != (state?.MaintenanceGeneration ?? 0))) throw new ProjectMaintenanceException();
    }
}
