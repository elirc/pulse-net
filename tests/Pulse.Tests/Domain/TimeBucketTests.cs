using Pulse.Domain;

namespace Pulse.Tests.Domain;

public class TimeBucketTests
{
    [Fact]
    public void Truncate_Hour_DropsMinutesAndSeconds()
    {
        var ts = new DateTimeOffset(2026, 5, 10, 14, 37, 59, TimeSpan.Zero);

        var bucket = TimeBucket.Truncate(ts, TrendInterval.Hour);

        Assert.Equal(new DateTimeOffset(2026, 5, 10, 14, 0, 0, TimeSpan.Zero), bucket);
    }

    [Fact]
    public void Truncate_Day_DropsTimeOfDay()
    {
        var ts = new DateTimeOffset(2026, 5, 10, 23, 59, 59, TimeSpan.Zero);

        var bucket = TimeBucket.Truncate(ts, TrendInterval.Day);

        Assert.Equal(new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero), bucket);
    }

    [Fact]
    public void Truncate_Day_NormalizesOffsetsToUtc()
    {
        // 01:30 +08:00 is 17:30 UTC the previous day.
        var ts = new DateTimeOffset(2026, 5, 10, 1, 30, 0, TimeSpan.FromHours(8));

        var bucket = TimeBucket.Truncate(ts, TrendInterval.Day);

        Assert.Equal(new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.Zero), bucket);
    }

    [Theory]
    [InlineData(2026, 5, 11)] // Monday
    [InlineData(2026, 5, 13)] // Wednesday
    [InlineData(2026, 5, 17)] // Sunday
    public void Truncate_Week_SnapsToMonday(int year, int month, int day)
    {
        var ts = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero);

        var bucket = TimeBucket.Truncate(ts, TrendInterval.Week);

        Assert.Equal(new DateTimeOffset(2026, 5, 11, 0, 0, 0, TimeSpan.Zero), bucket);
        Assert.Equal(DayOfWeek.Monday, bucket.DayOfWeek);
    }

    [Fact]
    public void Range_CoversFromAndToInclusive()
    {
        var from = new DateTimeOffset(2026, 5, 1, 13, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 5, 4, 2, 0, 0, TimeSpan.Zero);

        var buckets = TimeBucket.Range(from, to, TrendInterval.Day).ToList();

        Assert.Equal(4, buckets.Count);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), buckets[0]);
        Assert.Equal(new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero), buckets[^1]);
    }
}
