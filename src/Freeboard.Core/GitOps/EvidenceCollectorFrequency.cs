namespace Freeboard.Core.GitOps;

/// <summary>
/// The canonical evidence-collector collection cadence vocabulary and the pure staleness rule derived
/// from it. Owns the closed frequency token set (reused by <see cref="ConfigValidator"/>) and the
/// per-cadence expectation: a <c>window</c> (the maximum expected interval between collections) plus a
/// <c>grace</c> (a proportional allowance for one late cycle's jitter and clock skew). A run is stale
/// once its age exceeds window + grace. Pure and clock-free: callers pass the current UTC instant.
/// </summary>
public static class EvidenceCollectorFrequency
{
    /// <summary>Closed token set for a collector's collection cadence (case-sensitive).</summary>
    public static readonly IReadOnlySet<string> Tokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "continuous", "daily", "weekly", "monthly", "quarterly", "annual",
    };

    // Windows sit at the upper bound of each period (31/92/366 days) so a boundary collection never
    // false-flags despite calendar month/quarter length; grace shrinks as a fraction of the window as the
    // cadence lengthens, so a fixed absolute grace never doubles a short cadence's tolerance.
    private static readonly IReadOnlyDictionary<string, (TimeSpan Window, TimeSpan Grace)> Cadences =
        new Dictionary<string, (TimeSpan, TimeSpan)>(StringComparer.Ordinal)
        {
            ["continuous"] = (TimeSpan.FromHours(1), TimeSpan.FromMinutes(15)),
            ["daily"] = (TimeSpan.FromDays(1), TimeSpan.FromHours(6)),
            ["weekly"] = (TimeSpan.FromDays(7), TimeSpan.FromDays(1)),
            ["monthly"] = (TimeSpan.FromDays(31), TimeSpan.FromDays(3)),
            ["quarterly"] = (TimeSpan.FromDays(92), TimeSpan.FromDays(7)),
            ["annual"] = (TimeSpan.FromDays(366), TimeSpan.FromDays(30)),
        };

    /// <summary>
    /// The scheduling interval for <paramref name="frequency"/>: the cadence window (the maximum expected
    /// interval between collections, <c>continuous</c> 1h ... <c>annual</c> 366d), or null for a null,
    /// blank, or unknown token. This is the plain window with no grace: grace is a staleness tolerance,
    /// not a scheduling delay, so firing at the interval keeps evidence refreshed before it can cross the
    /// staleness threshold (window + grace).
    /// </summary>
    public static TimeSpan? Interval(string? frequency)
    {
        if (string.IsNullOrWhiteSpace(frequency) || !Cadences.TryGetValue(frequency, out var cadence))
        {
            return null;
        }

        return cadence.Window;
    }

    /// <summary>
    /// True when a run collected at <paramref name="collectedAtUtc"/> is overdue at
    /// <paramref name="nowUtc"/> for its recorded <paramref name="frequency"/>. A null, blank, or unknown
    /// cadence yields no window and is never stale, so a run with no recorded cadence keeps its last-known
    /// verdict.
    /// </summary>
    public static bool IsStale(DateTime collectedAtUtc, string? frequency, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(frequency) || !Cadences.TryGetValue(frequency, out var cadence))
        {
            return false;
        }

        return nowUtc - collectedAtUtc > cadence.Window + cadence.Grace;
    }
}
