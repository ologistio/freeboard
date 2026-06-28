using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// Argon2id password hasher backed by Konscious. The out-of-band pepper is mixed
/// in as Argon2's native KEYED secret (<c>Argon2id.KnownSecret</c>), so a DB-only
/// compromise cannot offline-attack hashes without the secret and there is no
/// concatenation-ambiguity. Output is a self-describing PHC-style string that records
/// the parameters, the keyed-secret version, the salt, and the hash.
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    // OWASP 2024 baseline for Argon2id (memory 19 MiB, t=2, p=1).
    private const int MemoryKiB = 19 * 1024;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    private readonly AuthCryptoOptions options;
    private readonly string decoyHash;

    public Argon2idPasswordHasher(AuthCryptoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AuthKeyMaterial.Validate(
            options.PasswordSecrets, options.CurrentPasswordSecretVersion, "AuthCryptoOptions.PasswordSecrets");
        this.options = options;

        // A fixed decoy hash with a fixed salt, computed once, so VerifyDecoy and an empty
        // submitted password do the same memory-hard work as a real verify.
        this.decoyHash = ComputeHash(
            "freeboard-decoy-password",
            new byte[SaltLength],
            options.CurrentPasswordSecretVersion);
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0)
        {
            // An empty password must never be stored; callers validate non-empty before
            // hashing. Failing here stops an empty credential from ever being persisted.
            throw new ArgumentException("Password must not be empty.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        return ComputeHash(password, salt, options.CurrentPasswordSecretVersion);
    }

    public bool Verify(string password, string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(password);

        // An empty submitted password can never match a real credential. Still do
        // equivalent Argon2 work against the decoy so the timing matches a real verify,
        // then return false.
        if (password.Length == 0)
        {
            _ = VerifyNonEmpty("freeboard-decoy-password", decoyHash);
            return false;
        }

        return VerifyNonEmpty(password, encodedHash);
    }

    public bool NeedsRehash(string encodedHash)
    {
        if (!PhcHash.TryParse(encodedHash, out var parsed))
        {
            // Unparseable: treat as needing a rehash so a successful login replaces it.
            return true;
        }

        return parsed.SecretVersion != options.CurrentPasswordSecretVersion
            || parsed.MemoryKiB != MemoryKiB
            || parsed.Iterations != Iterations
            || parsed.Parallelism != Parallelism;
    }

    public bool VerifyDecoy(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        // Same work as Verify, always false. The result is discarded.
        var probe = password.Length == 0 ? "freeboard-decoy-password" : password;
        _ = VerifyNonEmpty(probe, decoyHash);
        return false;
    }

    private bool VerifyNonEmpty(string password, string encodedHash)
    {
        if (!PhcHash.TryParse(encodedHash, out var parsed))
        {
            return false;
        }

        if (!options.PasswordSecrets.TryGetValue(parsed.SecretVersion, out var secret))
        {
            return false;
        }

        byte[] computed;
        try
        {
            computed = Compute(
                password, parsed.Salt, secret, parsed.MemoryKiB, parsed.Iterations, parsed.Parallelism, parsed.Hash.Length);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // A corrupt/hostile hash row must fail closed, never crash a login.
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(computed, parsed.Hash);
    }

    private string ComputeHash(string password, byte[] salt, int secretVersion)
    {
        var secret = options.PasswordSecrets[secretVersion];
        var hash = Compute(password, salt, secret, MemoryKiB, Iterations, Parallelism, HashLength);
        return PhcHash.Format(MemoryKiB, Iterations, Parallelism, secretVersion, salt, hash);
    }

    private static byte[] Compute(
        string password, byte[] salt, byte[] secret, int memoryKiB, int iterations, int parallelism, int outputLength)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            KnownSecret = secret,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(outputLength);
    }
}
