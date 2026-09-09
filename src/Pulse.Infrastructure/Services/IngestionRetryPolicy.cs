using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Services;

public record IngestionRetryDecision(bool DeadLetter, int FailedAttempts, TimeSpan? Delay, string Code);
public static class IngestionRetryPolicy
{
    public const int MaxAttempts = 5;
    public static bool IsTransient(Exception error)
    {
        var root = error is DbUpdateException { InnerException: { } inner } ? inner : error;
        return root is SqliteException { SqliteErrorCode: 5 or 6 }; // SQLITE_BUSY / SQLITE_LOCKED only
    }
    public static IngestionRetryDecision AfterTransientFailure(int previousFailedAttempts)
    {
        var failed = checked(previousFailedAttempts + 1);
        return failed >= MaxAttempts ? new(true, failed, null, "sqlite_contention_exhausted")
            : new(false, failed, TimeSpan.FromSeconds(1 << (failed - 1)), "sqlite_contention");
    }
}
