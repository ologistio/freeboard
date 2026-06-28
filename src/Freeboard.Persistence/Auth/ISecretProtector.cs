namespace Freeboard.Persistence.Auth;

/// <summary>
/// A protected secret: AES-256-GCM ciphertext with its nonce, authentication tag, and the
/// key version it was sealed under. The plaintext is recoverable only with the matching
/// out-of-band key, so a DB dump alone cannot reveal a TOTP secret.
/// </summary>
public readonly record struct ProtectedSecret(byte[] Ciphertext, byte[] Nonce, byte[] Tag, int KeyVersion);

/// <summary>
/// Authenticated encryption for at-rest secrets that need a recoverable plaintext (TOTP
/// secrets). AES-256-GCM under a REQUIRED out-of-band key, versioned for rotation like
/// the token/password key sets. Fails loudly if no key is configured. Storage-agnostic: no
/// SQL types in the contract.
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    /// Seals <paramref name="plaintext"/> under the current key version. Returns the
    /// ciphertext, a fresh 12-byte nonce, the 16-byte GCM tag, and the key version.
    /// </summary>
    ProtectedSecret Protect(byte[] plaintext);

    /// <summary>
    /// Recovers the plaintext sealed in <paramref name="secret"/>, selecting the key by its
    /// stored version. Throws if the version names no retained key or the tag fails (tamper).
    /// </summary>
    byte[] Unprotect(ProtectedSecret secret);
}
