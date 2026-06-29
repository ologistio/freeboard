using System.Data;
using Dapper;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// MySQL-backed <see cref="IAuthRateLimitStore"/>. The check-and-increment is made atomic by
/// first seeding the bucket with <c>INSERT IGNORE</c> (so a concurrent first attempt cannot
/// both land <c>attempt_count = 1</c>), then locking the row with <c>SELECT ... FOR UPDATE</c>
/// on the <c>(bucket_kind, bucket_key)</c> composite PK. The seed defaults the row to a count
/// of 0 in the current window, so the locked update always increments an existing row. The
/// window-rollover and lock decision is the pure <see cref="RateLimitDecision.Decide"/>, then
/// written back under the same lock.
/// </summary>
public sealed class MySqlAuthRateLimitStore(IDbConnectionFactory connectionFactory) : IAuthRateLimitStore
{
    public async Task<RateLimitResult> CheckAndIncrementAsync(
        RateLimitBucketKind kind,
        string key,
        int limit,
        TimeSpan window,
        TimeSpan lockout,
        CancellationToken cancellationToken = default)
    {
        var bucketKind = kind.ToString();
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        // Seed the bucket atomically. INSERT IGNORE makes exactly one concurrent first attempt
        // create the row (count 0, current window); the loser's insert is a no-op. Both callers
        // then lock the SAME row below and serialize their increments, so two concurrent first
        // attempts cannot both write attempt_count = 1.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO auth_rate_limits (bucket_kind, bucket_key, attempt_count, window_started_at, locked_until) "
            + "VALUES (@Kind, @Key, 0, @Now, NULL);",
            new { Kind = bucketKind, Key = key, Now = now },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var existing = await connection.QuerySingleAsync<(int AttemptCount, DateTime WindowStartedAt, DateTime? LockedUntil)>(
            new CommandDefinition(
                "SELECT attempt_count AS AttemptCount, window_started_at AS WindowStartedAt, locked_until AS LockedUntil "
                + "FROM auth_rate_limits WHERE bucket_kind = @Kind AND bucket_key = @Key FOR UPDATE;",
                new { Kind = bucketKind, Key = key },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var decision = RateLimitDecision.Decide(
            existing.AttemptCount, existing.WindowStartedAt, existing.LockedUntil, now, limit, window, lockout);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE auth_rate_limits SET attempt_count = @AttemptCount, window_started_at = @WindowStartedAt, "
            + "locked_until = @LockedUntil WHERE bucket_kind = @Kind AND bucket_key = @Key;",
            new
            {
                Kind = bucketKind,
                Key = key,
                decision.AttemptCount,
                decision.WindowStartedAt,
                decision.LockedUntil,
            },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return decision.LockedUntil is { } until && until > now
            ? new RateLimitResult(true, decision.AttemptCount, until - now)
            : new RateLimitResult(false, decision.AttemptCount, TimeSpan.Zero);
    }

    public async Task ResetAccountAsync(string accountKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM auth_rate_limits WHERE bucket_kind = @Kind AND bucket_key = @Key;",
            new { Kind = RateLimitBucketKind.Account.ToString(), Key = accountKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - retention;
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Only prune rows whose window is old AND that are not currently locked.
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM auth_rate_limits "
            + "WHERE window_started_at <= @Cutoff AND (locked_until IS NULL OR locked_until <= @Now);",
            new { Cutoff = cutoff, Now = now },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}

/// <summary>
/// The pure rate-limit state transition, unit-testable without a DB. Given the stored
/// bucket state and the current time, computes the next count, window start, and lock.
/// </summary>
/// <param name="AttemptCount">The next attempt count to persist.</param>
/// <param name="WindowStartedAt">The next window start to persist.</param>
/// <param name="LockedUntil">The next lock expiry to persist (null when unlocked).</param>
public readonly record struct RateLimitDecision(int AttemptCount, DateTime WindowStartedAt, DateTime? LockedUntil)
{
    public static RateLimitDecision Decide(
        int storedCount,
        DateTime storedWindowStartedAt,
        DateTime? storedLockedUntil,
        DateTime now,
        int limit,
        TimeSpan window,
        TimeSpan lockout)
    {
        // SECURITY: honor an active lock FIRST. A bucket that is still locked stays
        // locked until locked_until passes, regardless of window rollover - the rollover must
        // never clear a live lock. Keep counting up so the lock is not extended spuriously.
        if (storedLockedUntil is { } locked && locked > now)
        {
            return new RateLimitDecision(storedCount + 1, storedWindowStartedAt, locked);
        }

        // No active lock: roll the window if the prior one elapsed, else increment in place.
        if (storedWindowStartedAt <= now - window)
        {
            return new RateLimitDecision(1, now, null);
        }

        var attemptCount = storedCount + 1;
        return new RateLimitDecision(
            attemptCount, storedWindowStartedAt, attemptCount >= limit ? now + lockout : null);
    }
}
