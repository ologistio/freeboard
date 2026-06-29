namespace Freeboard.Persistence.Auth;

/// <summary>
/// Validates that a versioned key/secret set is present and strong enough. Any
/// null/empty entry, a missing current version, or an entry of the wrong length fails loudly
/// with a message naming the offending version, so weak/malformed crypto material cannot reach
/// runtime. HMAC keys and the Argon2 keyed secret (pepper) require AT LEAST 32 bytes; the
/// AES-256-GCM secret-protection key requires EXACTLY 32 bytes, since AesGcm rejects any
/// other length at first use - validating it here fails at startup instead of at first TOTP seal.
/// </summary>
internal static class AuthKeyMaterial
{
    /// <summary>Minimum length for HMAC keys and the Argon2 keyed secret (pepper), in bytes.</summary>
    public const int MinimumKeyBytes = 32;

    /// <summary>Exact length required for the AES-256-GCM secret-protection key, in bytes.</summary>
    public const int AesKeyBytes = 32;

    /// <summary>Validates an HMAC/pepper key set: each key at least <see cref="MinimumKeyBytes"/>.</summary>
    public static void Validate(IReadOnlyDictionary<int, byte[]> keys, int currentVersion, string name)
        => Validate(keys, currentVersion, name, exactLength: false);

    /// <summary>
    /// Validates a key set. When <paramref name="exactLength"/> is true every key must be EXACTLY
    /// <see cref="AesKeyBytes"/> bytes (AES-256-GCM); otherwise at least <see cref="MinimumKeyBytes"/>.
    /// </summary>
    public static void Validate(
        IReadOnlyDictionary<int, byte[]> keys, int currentVersion, string name, bool exactLength)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"{name} is empty. It is REQUIRED and must be supplied out-of-band (env/user-secrets/config).");
        }

        foreach (var (version, key) in keys)
        {
            if (key is null || key.Length == 0)
            {
                throw new InvalidOperationException($"{name} version {version} is null or empty.");
            }

            if (exactLength)
            {
                if (key.Length != AesKeyBytes)
                {
                    throw new InvalidOperationException(
                        $"{name} version {version} is {key.Length} bytes; EXACTLY {AesKeyBytes} bytes are required (AES-256-GCM).");
                }
            }
            else if (key.Length < MinimumKeyBytes)
            {
                throw new InvalidOperationException(
                    $"{name} version {version} is {key.Length} bytes; at least {MinimumKeyBytes} bytes are required.");
            }
        }

        if (!keys.ContainsKey(currentVersion))
        {
            throw new InvalidOperationException(
                $"{name} current version {currentVersion} has no matching entry.");
        }
    }
}
