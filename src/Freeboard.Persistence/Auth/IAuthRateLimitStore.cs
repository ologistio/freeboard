namespace Freeboard.Persistence.Auth;

/// <summary>A rate-limit bucket kind. SEPARATE per-account and per-IP buckets each trip independently.</summary>
public enum RateLimitBucketKind
{
    /// <summary>Keyed by normalized email. Locks unknown emails too, so a 429 is enumeration-safe.</summary>
    Account,

    /// <summary>Keyed by trusted client IP. Persists across a successful account reset.</summary>
    Ip,
}

/// <summary>
/// The outcome of an atomic check-and-increment. <paramref name="Limited"/> is true when the
/// bucket is currently locked; <paramref name="RetryAfter"/> is the wait until the lock
/// clears (zero when not limited).
/// </summary>
public readonly record struct RateLimitResult(bool Limited, int AttemptCount, TimeSpan RetryAfter);

/// <summary>
/// Storage-agnostic rate-limit store. No SQL/Dapper types leak into the
/// contract, so a Redis (INCR/EXPIRE or a Lua check-and-increment) impl can replace the
/// MySQL default. Buckets are separate per kind+key; a request is limited if ANY applicable
/// bucket trips.
/// </summary>
public interface IAuthRateLimitStore
{
    /// <summary>
    /// Atomically records an attempt against one bucket and returns the resulting state.
    /// <paramref name="limit"/> attempts are allowed within <paramref name="window"/>; once
    /// the limit is reached the bucket is locked for <paramref name="lockout"/>. Unknown
    /// emails are locked the same as known ones (enumeration-safe).
    /// </summary>
    Task<RateLimitResult> CheckAndIncrementAsync(
        RateLimitBucketKind kind,
        string key,
        int limit,
        TimeSpan window,
        TimeSpan lockout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets ONLY the account bucket on a successful authentication. The IP bucket is left
    /// intact so a shared/abusive IP keeps accruing.
    /// </summary>
    Task ResetAccountAsync(string accountKey, CancellationToken cancellationToken = default);

    /// <summary>Removes stale, no-longer-locked rows older than <paramref name="retention"/>. Returns the count pruned.</summary>
    Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken = default);
}
