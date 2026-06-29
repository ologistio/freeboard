namespace Freeboard.Persistence.Auth;

/// <summary>
/// Non-secret auth configuration (durations, counts). Secrets/keys live in
/// <see cref="AuthCryptoOptions"/>. All values have documented defaults and are
/// config-tunable.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Absolute session lifetime. Default 24h.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Number of recovery codes generated as a set. Default 10.</summary>
    public int RecoveryCodeCount { get; init; } = 10;

    /// <summary>Max failed MFA-challenge attempts before the challenge auto-consumes. Default 5.</summary>
    public int MfaMaxAttempts { get; init; } = 5;

    /// <summary>Max magic-link re-sends per challenge. Default 3.</summary>
    public int MagicLinkMaxSends { get; init; } = 3;
}
