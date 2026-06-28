namespace Freeboard.Persistence.Auth;

/// <summary>The result of enrolling TOTP: the otpauth:// provisioning URI shown once for QR/manual entry.</summary>
/// <param name="ProvisioningUri">The otpauth://totp/... URI. Contains the base32 secret; shown once.</param>
public readonly record struct TotpEnrollment(string ProvisioningUri);

/// <summary>
/// TOTP store. One secret per user, ENCRYPTED at rest via <see cref="ISecretProtector"/>.
/// Enrollment generates a secret and returns the provisioning URI; activation requires a
/// confirming code and stamps <c>confirmed_at</c>. Verification uses a +/-1 step window and
/// advances <c>last_time_step</c> atomically so a code cannot be replayed within its step.
/// </summary>
public interface ITotpStore
{
    /// <summary>
    /// Generates a new secret for the user, encrypts it, and stores it UNCONFIRMED (replacing
    /// any prior unconfirmed/confirmed secret). Returns the otpauth:// provisioning URI.
    /// </summary>
    Task<TotpEnrollment> EnrollAsync(string userId, string accountName, string issuer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms enrollment: verifies <paramref name="code"/> against the stored secret and, on
    /// success, stamps <c>confirmed_at</c> and advances the replay step. Returns true if the
    /// code was valid and not a replay.
    /// </summary>
    Task<bool> ActivateAsync(string userId, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies <paramref name="code"/> for a CONFIRMED secret and atomically advances
    /// <c>last_time_step</c>, rejecting a replay within the same step. Returns false if there is
    /// no confirmed secret, the code is invalid, or the step was already consumed.
    /// </summary>
    Task<bool> VerifyAsync(string userId, string code, CancellationToken cancellationToken = default);

    /// <summary>True if the user has a confirmed TOTP secret.</summary>
    Task<bool> IsConfirmedAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Removes the user's TOTP secret. Returns true if a row was deleted.</summary>
    Task<bool> DeleteAsync(string userId, CancellationToken cancellationToken = default);
}
