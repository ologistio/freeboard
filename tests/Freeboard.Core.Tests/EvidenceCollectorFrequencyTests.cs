using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the pure staleness rule: each cadence's window-plus-grace boundary (just-inside vs just-over),
/// the sub-day continuous window judged in hours, and that a null, blank, or unknown cadence is never
/// stale. Clock-free: every case passes an explicit now.
/// </summary>
public sealed class EvidenceCollectorFrequencyTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    // Each cadence's threshold (window + grace) as an age. A run older than this is stale.
    [Theory]
    [InlineData("continuous", 1, 15)] // 1h 15m
    [InlineData("daily", 30, 0)] // 30h
    [InlineData("weekly", 8 * 24, 0)] // 8d
    [InlineData("monthly", 34 * 24, 0)] // 34d
    [InlineData("quarterly", 99 * 24, 0)] // 99d
    [InlineData("annual", 396 * 24, 0)] // 396d
    public void JustInsideWindowIsFresh(string frequency, int hours, int minutes)
    {
        var threshold = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        // A run aged one second short of the threshold is not yet stale.
        var collectedAt = Now - threshold + TimeSpan.FromSeconds(1);
        Assert.False(EvidenceCollectorFrequency.IsStale(collectedAt, frequency, Now));
    }

    [Theory]
    [InlineData("continuous", 1, 15)]
    [InlineData("daily", 30, 0)]
    [InlineData("weekly", 8 * 24, 0)]
    [InlineData("monthly", 34 * 24, 0)]
    [InlineData("quarterly", 99 * 24, 0)]
    [InlineData("annual", 396 * 24, 0)]
    public void JustOverWindowIsStale(string frequency, int hours, int minutes)
    {
        var threshold = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        // A run aged one second past the threshold is stale.
        var collectedAt = Now - threshold - TimeSpan.FromSeconds(1);
        Assert.True(EvidenceCollectorFrequency.IsStale(collectedAt, frequency, Now));
    }

    [Fact]
    public void ContinuousWindowIsJudgedInHours()
    {
        // 90 minutes exceeds continuous's 75-minute threshold (1h window + 15m grace).
        Assert.True(EvidenceCollectorFrequency.IsStale(Now - TimeSpan.FromMinutes(90), "continuous", Now));
        // 60 minutes is inside it.
        Assert.False(EvidenceCollectorFrequency.IsStale(Now - TimeSpan.FromMinutes(60), "continuous", Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hourly")]
    [InlineData("Daily")] // token match is case-sensitive
    public void NullBlankOrUnknownCadenceIsNeverStale(string? frequency)
    {
        // Even an ancient run yields no verdict of stale without a known cadence.
        var collectedAt = Now - TimeSpan.FromDays(3650);
        Assert.False(EvidenceCollectorFrequency.IsStale(collectedAt, frequency, Now));
    }
}
