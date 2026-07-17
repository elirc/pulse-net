namespace Pulse.Domain;

public enum TrendInterval
{
    Hour,
    Day,
    Week,
}

/// <summary>Pure time-bucketing math shared by the query engine.</summary>
public static class TimeBucket
{
    /// <summary>Truncates a timestamp (in UTC) to the start of its bucket. Weeks start on Monday.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset timestamp, TrendInterval interval)
    {
        var utc = timestamp.ToUniversalTime();

        return interval switch
        {
            TrendInterval.Hour => new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero),
            TrendInterval.Day => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero),
            TrendInterval.Week => TruncateToWeek(utc),
            _ => throw new ArgumentOutOfRangeException(nameof(interval)),
        };
    }

    /// <summary>Advances a bucket start to the next bucket.</summary>
    public static DateTimeOffset Next(DateTimeOffset bucketStart, TrendInterval interval) => interval switch
    {
        TrendInterval.Hour => bucketStart.AddHours(1),
        TrendInterval.Day => bucketStart.AddDays(1),
        TrendInterval.Week => bucketStart.AddDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };

    /// <summary>Enumerates every bucket start covering [from, to], zero-fill friendly.</summary>
    public static IEnumerable<DateTimeOffset> Range(DateTimeOffset from, DateTimeOffset to, TrendInterval interval)
    {
        for (var bucket = Truncate(from, interval); bucket <= to; bucket = Next(bucket, interval))
        {
            yield return bucket;
        }
    }

    private static DateTimeOffset TruncateToWeek(DateTimeOffset utc)
    {
        var day = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        var offset = ((int)day.DayOfWeek + 6) % 7; // Monday = 0
        return day.AddDays(-offset);
    }
}
