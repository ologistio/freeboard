namespace Freeboard.Auth;

/// <summary>
/// Web-side auth options bound from the <c>Auth</c> config section. Crypto material and the
/// durable defaults (session lifetime, MFA caps) live in the persistence layer's options; this
/// holds the web pipeline tunables (sudo TTL, rate-limit thresholds) with documented defaults.
/// </summary>
public sealed class WebAuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Sudo-mode (step-up) TTL. A session's sudo_at must be within this window. Default 5 minutes.</summary>
    public TimeSpan SudoModeTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Max attempts in a rate-limit window before a bucket locks. Default 10.</summary>
    public int RateLimitMaxAttempts { get; set; } = 10;

    /// <summary>The rate-limit window. Default 15 minutes.</summary>
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The lockout once a bucket trips. Default 15 minutes.</summary>
    public TimeSpan RateLimitLockout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Absolute session lifetime. Default 24h.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Password-reset token lifetime. Default 1 hour.</summary>
    public TimeSpan PasswordResetLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether the password-reset (forgot/reset) flow is enabled. When true, an
    /// <see cref="Persistence.Auth.IAuthEmailSender"/> MUST be registered or the app fails fast
    /// at startup, so the runtime behavior of forgot-password stays uniform. Default false.
    /// </summary>
    public bool PasswordResetEnabled { get; set; }

    /// <summary>
    /// The one-time first-admin bootstrap secret, supplied out-of-band
    /// (FREEBOARD_BOOTSTRAP_SECRET / Auth:BootstrapSecret). A wrong/absent secret rejects setup
    /// with 401. Empty means setup is disabled.
    /// </summary>
    public string BootstrapSecret { get; set; } = string.Empty;

    /// <summary>MFA login-challenge lifetime. Default 10 minutes.</summary>
    public TimeSpan MfaChallengeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Max failed MFA-verify attempts before the challenge auto-consumes. Default 5.</summary>
    public int MfaMaxAttempts { get; set; } = 5;

    /// <summary>Magic-link token lifetime. Default 10 minutes.</summary>
    public TimeSpan MagicLinkLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Max magic-link re-sends per challenge. Default 3.</summary>
    public int MagicLinkMaxSends { get; set; } = 3;

    /// <summary>Recovery-code count generated as a set. Default 10.</summary>
    public int RecoveryCodeCount { get; set; } = 10;
}

/// <summary>
/// WebAuthn/FIDO2 config bound from <c>Auth:WebAuthn</c>. RP id and allowed origins are
/// EXPLICIT REQUIRED outside Development; the ceremony service fails if they are unconfigured
/// outside dev so a deployment cannot silently accept any origin.
/// </summary>
public sealed class WebAuthnOptions
{
    public const string SectionName = "Auth:WebAuthn";

    /// <summary>The Relying Party id (registrable domain), e.g. <c>freeboard.example</c>.</summary>
    public string RpId { get; set; } = string.Empty;

    /// <summary>A human-readable RP name shown by authenticators.</summary>
    public string RpName { get; set; } = "Freeboard";

    /// <summary>The allowed origins (full scheme+host[+port]), e.g. <c>https://freeboard.example</c>.</summary>
    public string[] Origins { get; set; } = [];
}
