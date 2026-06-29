using System.Security.Cryptography;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// AES-256-GCM <see cref="ISecretProtector"/> over the versioned key set in
/// <see cref="AuthCryptoOptions.SecretProtectionKeys"/>. A fresh 12-byte nonce is
/// generated per seal; the 16-byte GCM tag authenticates the ciphertext so tampering fails
/// loudly on unseal. The key version is stored with the ciphertext, so rotation keeps old
/// ciphertexts decryptable under the version that sealed them.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int NonceLength = 12; // AES-GCM standard nonce.
    private const int TagLength = 16;   // AES-GCM standard tag.

    private readonly AuthCryptoOptions options;

    public AesGcmSecretProtector(AuthCryptoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // AES-256-GCM requires an EXACTLY 32-byte key; a wrong length must fail here at
        // construction, not later at the first seal inside AesGcm.
        AuthKeyMaterial.Validate(
            options.SecretProtectionKeys,
            options.CurrentSecretProtectionKeyVersion,
            "AuthCryptoOptions.SecretProtectionKeys",
            exactLength: true);
        this.options = options;
    }

    public ProtectedSecret Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var version = options.CurrentSecretProtectionKeyVersion;
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(options.SecretProtectionKeys[version], TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return new ProtectedSecret(ciphertext, nonce, tag, version);
    }

    public byte[] Unprotect(ProtectedSecret secret)
    {
        if (!options.SecretProtectionKeys.TryGetValue(secret.KeyVersion, out var key))
        {
            throw new InvalidOperationException(
                $"AuthCryptoOptions.SecretProtectionKeys version {secret.KeyVersion} has no matching entry.");
        }

        var plaintext = new byte[secret.Ciphertext.Length];
        using var aes = new AesGcm(key, TagLength);
        // Throws AuthenticationTagMismatchException (a CryptographicException) on tamper.
        aes.Decrypt(secret.Nonce, secret.Ciphertext, secret.Tag, plaintext);
        return plaintext;
    }
}
