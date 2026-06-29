namespace Freeboard.Persistence.Auth;

/// <summary>A minted token: the value to return to the client once, plus the key id its HMAC was computed under.</summary>
/// <param name="Token">The wire value handed to the client. Never stored.</param>
/// <param name="Hash">The keyed HMAC-SHA256 digest stored at rest (32 bytes).</param>
/// <param name="KeyVersion">The token key id used. Stored alongside the hash.</param>
public readonly record struct MintedToken(string Token, byte[] Hash, int KeyVersion);

/// <summary>
/// Keyed token hashing over a versioned HMAC-SHA256 key set. All server-issued or
/// server-verified secrets are stored as a keyed HMAC, never bare SHA-256, so a dumped
/// table cannot be turned into a lookup table without the server key.
///
/// Two modes:
/// - PREFIX-BEARING tokens (session, password-reset, MFA-challenge) carry the key id in
///   their wire format <c>v&lt;keyId&gt;.&lt;secret-base64url&gt;</c>. Verification parses the
///   key id FROM THE TOKEN, so a malformed or unknown-key token is rejected WITHOUT a DB
///   lookup (no valid hash can be computed).
/// - PREFIXLESS tokens (recovery codes, magic-link) carry no prefix; the signing key id
///   comes from an explicitly-supplied STORED key version, so they keep verifying after a
///   rotation because the stored version still names a retained key.
/// </summary>
public interface ITokenHasher
{
    /// <summary>
    /// Mints a new prefix-bearing token: 32 CSPRNG bytes wrapped as
    /// <c>v&lt;currentKeyId&gt;.&lt;secret-base64url&gt;</c>, with the HMAC computed under the
    /// current key id.
    /// </summary>
    MintedToken MintPrefixed();

    /// <summary>
    /// Mints a new prefixless secret (no key-id prefix) hashed under the current key id.
    /// The caller stores the returned key version (e.g. recovery-code / magic-link key
    /// column) to verify later. <paramref name="secretLength"/> bytes of CSPRNG entropy;
    /// must be at least 32.
    /// </summary>
    MintedToken MintPrefixless(int secretLength = 32);

    /// <summary>
    /// Computes the at-rest hash for an already-formed prefixless secret (e.g. a single
    /// recovery code from a batch) under the current key id.
    /// </summary>
    MintedToken HashPrefixless(string secret);

    /// <summary>
    /// Parses and HMACs a prefix-bearing token. Returns false (no hash) when the token is
    /// malformed or names an unknown key id, so the caller can reject it without a DB
    /// lookup. On success, <paramref name="hash"/> is the lookup digest and
    /// <paramref name="keyVersion"/> is the parsed key id (assert it equals the stored
    /// column as an integrity check).
    /// </summary>
    bool TryHashPrefixed(string token, out byte[] hash, out int keyVersion);

    /// <summary>
    /// HMACs a prefixless token under the explicitly-supplied stored
    /// <paramref name="keyVersion"/>. Returns false when that version names no retained
    /// key. The caller compares the result against the stored hash with a constant-time
    /// comparison.
    /// </summary>
    bool TryHashPrefixless(string token, int keyVersion, out byte[] hash);

    /// <summary>
    /// Constant-time verify of a prefix-bearing token against the row's stored hash, so
    /// stores never hand-roll the comparison. Parses the key id from the token,
    /// computes the HMAC, and compares with
    /// <see cref="System.Security.Cryptography.CryptographicOperations"/> FixedTimeEquals.
    /// Returns false (no comparison) for malformed/unknown-key tokens. Use
    /// <see cref="TryHashPrefixed"/> to compute the lookup hash, then this to verify the
    /// located row.
    /// </summary>
    bool VerifyPrefixed(string token, ReadOnlySpan<byte> expectedHash);

    /// <summary>
    /// Constant-time verify of a prefixless token under the explicitly-supplied stored
    /// key version. Returns false when the version names no retained key.
    /// </summary>
    bool VerifyPrefixless(string token, int keyVersion, ReadOnlySpan<byte> expectedHash);
}
