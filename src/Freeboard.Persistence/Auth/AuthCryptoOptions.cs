namespace Freeboard.Persistence.Auth;

/// <summary>
/// Out-of-band crypto material for auth, supplied via env/user-secrets/config and never
/// committed. All values are REQUIRED; the consuming components fail loudly if they are
/// missing or malformed.
/// </summary>
public sealed class AuthCryptoOptions
{
    /// <summary>
    /// The Argon2 keyed-secret set (the pepper), versioned for rotation. The key is
    /// the secret version; the value is the raw secret bytes. The hasher mixes the
    /// current secret in as Argon2's native KEYED secret (Konscious <c>KnownSecret</c>),
    /// NOT by concatenation. Verify selects the secret by the version recorded in the PHC
    /// string so old hashes keep verifying after a rotation.
    /// </summary>
    public required IReadOnlyDictionary<int, byte[]> PasswordSecrets { get; init; }

    /// <summary>The secret version new hashes are produced under (the current version).</summary>
    public required int CurrentPasswordSecretVersion { get; init; }

    /// <summary>
    /// The HMAC-SHA256 token key set, versioned for rotation. The key is the key id;
    /// the value is the raw key bytes. Prefix-bearing tokens name their key id in the
    /// wire format; prefixless tokens verify under an explicitly-supplied stored version.
    /// </summary>
    public required IReadOnlyDictionary<int, byte[]> TokenKeys { get; init; }

    /// <summary>The token key id new tokens are minted under (the current key id).</summary>
    public required int CurrentTokenKeyVersion { get; init; }

    /// <summary>
    /// The AES-256-GCM secret-protection key set, versioned for rotation. The key is
    /// the key version; the value is the raw 32-byte key. Used by
    /// <see cref="ISecretProtector"/> to seal at-rest secrets whose plaintext must be
    /// recoverable (TOTP secrets). Unseal selects the key by the version stored alongside the
    /// ciphertext, so old ciphertexts keep decrypting after a rotation.
    /// </summary>
    public required IReadOnlyDictionary<int, byte[]> SecretProtectionKeys { get; init; }

    /// <summary>The secret-protection key version new ciphertexts are sealed under (the current version).</summary>
    public required int CurrentSecretProtectionKeyVersion { get; init; }
}
