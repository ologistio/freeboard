using Freeboard.Persistence.Auth;
using Microsoft.Extensions.Options;

namespace Freeboard.Auth;

/// <summary>
/// Reusable rate-limit enforcement for the auth endpoints. Endpoints call
/// <see cref="CheckAsync"/> BEFORE any expensive work (password verify, MFA factor check): it
/// trips both the per-account and per-IP buckets atomically and reports the first lock. On a
/// successful authentication the endpoint calls <see cref="ResetAccountAsync"/> so the account
/// bucket clears while the IP bucket persists. Thresholds come from <see cref="WebAuthOptions"/>.
/// </summary>
public sealed class AuthRateLimiter(IAuthRateLimitStore store, IOptions<WebAuthOptions> options)
{
    private readonly WebAuthOptions _options = options.Value;

    /// <summary>
    /// Checks both buckets for the given account key (normalized email) and client IP. Each
    /// bucket is checked even if an earlier one trips so both stay enumeration-safe and accrue.
    /// Returns <see cref="AuthRateLimitOutcome.Limited"/> with the longest retry-after when any
    /// bucket is locked, else <see cref="AuthRateLimitOutcome.Allowed"/>.
    /// </summary>
    public async Task<AuthRateLimitOutcome> CheckAsync(
        string accountKey, string? clientIp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountKey);

        var account = await store.CheckAndIncrementAsync(
            RateLimitBucketKind.Account, accountKey,
            _options.RateLimitMaxAttempts, _options.RateLimitWindow, _options.RateLimitLockout,
            cancellationToken).ConfigureAwait(false);

        RateLimitResult? ip = null;
        if (!string.IsNullOrEmpty(clientIp))
        {
            ip = await store.CheckAndIncrementAsync(
                RateLimitBucketKind.Ip, clientIp,
                _options.RateLimitMaxAttempts, _options.RateLimitWindow, _options.RateLimitLockout,
                cancellationToken).ConfigureAwait(false);
        }

        var retryAfter = TimeSpan.Zero;
        if (account.Limited)
        {
            retryAfter = account.RetryAfter;
        }

        if (ip is { Limited: true } i && i.RetryAfter > retryAfter)
        {
            retryAfter = i.RetryAfter;
        }

        var limited = account.Limited || (ip?.Limited ?? false);
        return new AuthRateLimitOutcome(limited, retryAfter);
    }

    /// <summary>Resets ONLY the account bucket after a successful authentication.</summary>
    public Task ResetAccountAsync(string accountKey, CancellationToken cancellationToken = default)
        => store.ResetAccountAsync(accountKey, cancellationToken);

    /// <summary>
    /// Writes a 429 with a Retry-After header for a limited outcome. Endpoints call this to
    /// produce a uniform throttle response.
    /// </summary>
    public static IResult Throttled(AuthRateLimitOutcome outcome)
    {
        var seconds = (int)Math.Ceiling(outcome.RetryAfter.TotalSeconds);
        return Results.Problem(
            title: "Too many requests",
            detail: "Rate limit exceeded. Retry later.",
            statusCode: StatusCodes.Status429TooManyRequests,
            type: "https://freeboard.io/problems/rate-limited",
            extensions: new Dictionary<string, object?> { ["retry_after"] = seconds });
    }
}

/// <summary>The outcome of a rate-limit check. <see cref="Limited"/> means deny with 429.</summary>
public readonly record struct AuthRateLimitOutcome(bool Limited, TimeSpan RetryAfter);
