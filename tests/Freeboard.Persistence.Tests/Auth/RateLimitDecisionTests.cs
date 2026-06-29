using Freeboard.Persistence.Auth;

namespace Freeboard.Persistence.Tests.Auth;

/// <summary>
/// Pure decision tests for the rate-limit state transition. No DB: covers window
/// rollover, lock-on-cap, and - critically - that an active lock is honored FIRST and is NOT
/// cleared by a window rollover.
/// </summary>
public sealed class RateLimitDecisionTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    private const int Limit = 5;

    [Fact]
    public void FirstAttemptInFreshWindowCountsOne()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // A seeded row at count 0 in the current window: first real attempt -> 1, no lock.
        var d = RateLimitDecision.Decide(0, now, null, now, Limit, Window, Lockout);
        Assert.Equal(1, d.AttemptCount);
        Assert.Equal(now, d.WindowStartedAt);
        Assert.Null(d.LockedUntil);
    }

    [Fact]
    public void ElapsedWindowRestartsCountAndClearsLock_WhenLockExpired()
    {
        var windowStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = windowStart + Window + TimeSpan.FromMinutes(1);
        // Lock already in the past: rollover is allowed and the count restarts at 1.
        var d = RateLimitDecision.Decide(5, windowStart, windowStart.AddMinutes(1), now, Limit, Window, Lockout);
        Assert.Equal(1, d.AttemptCount);
        Assert.Equal(now, d.WindowStartedAt);
        Assert.Null(d.LockedUntil);
    }

    [Fact]
    public void ReachingLimitLocksTheBucket()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var d = RateLimitDecision.Decide(4, now, null, now, Limit, Window, Lockout);
        Assert.Equal(5, d.AttemptCount);
        Assert.Equal(now + Lockout, d.LockedUntil);
    }

    [Fact]
    public void ActiveLockIsHonoredFirstAndNotClearedByWindowRollover()
    {
        // SECURITY: the window has elapsed BUT the lock is still active. The lock must be
        // preserved - a rollover must not unlock a locked bucket early.
        var windowStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lockedUntil = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        var now = windowStart + Window + TimeSpan.FromMinutes(1); // window elapsed
        Assert.True(now < lockedUntil); // but still locked

        var d = RateLimitDecision.Decide(7, windowStart, lockedUntil, now, Limit, Window, Lockout);

        Assert.Equal(lockedUntil, d.LockedUntil); // lock preserved
        Assert.Equal(windowStart, d.WindowStartedAt); // window NOT rolled
        Assert.Equal(8, d.AttemptCount); // attempt still counted
    }

    [Fact]
    public void IncrementInSameWindowKeepsWindowStart()
    {
        var windowStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = windowStart + TimeSpan.FromMinutes(2);
        var d = RateLimitDecision.Decide(2, windowStart, null, now, Limit, Window, Lockout);
        Assert.Equal(3, d.AttemptCount);
        Assert.Equal(windowStart, d.WindowStartedAt);
        Assert.Null(d.LockedUntil);
    }
}
