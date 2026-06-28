namespace Freeboard.Persistence.Auth;

/// <summary>
/// Password hashing seam. Hashes are self-describing (PHC-style) so parameters and
/// the keyed-secret version are encoded in the stored string and old hashes can be
/// upgraded on next successful login. The keyed secret (pepper) is mixed into the KDF as
/// Argon2's native secret parameter, not concatenated.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hashes <paramref name="password"/> under the current parameters and secret version.</summary>
    string Hash(string password);

    /// <summary>
    /// Verifies <paramref name="password"/> against <paramref name="encodedHash"/>. Returns
    /// false on any malformed/unknown-version hash rather than throwing, so a corrupt row
    /// cannot crash a login. Comparison is constant-time.
    /// </summary>
    bool Verify(string password, string encodedHash);

    /// <summary>
    /// True if <paramref name="encodedHash"/> was produced under older parameters or an
    /// older secret version than the current configuration, so it should be re-hashed on
    /// the next successful login.
    /// </summary>
    bool NeedsRehash(string encodedHash);

    /// <summary>
    /// Performs an equivalent-cost verify against a fixed decoy hash and always returns
    /// false. Called for unknown/disabled users so login does the same memory-hard
    /// work regardless of whether the account exists, defeating timing-based enumeration.
    /// </summary>
    bool VerifyDecoy(string password);
}
