using System.Globalization;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// A parsed PHC-style Argon2id hash string. Self-describing so parameters and the
/// keyed-secret version can be raised and old hashes upgraded on next login.
/// </summary>
/// <remarks>
/// Format: <c>$argon2id$v=19$m=&lt;mem&gt;,t=&lt;iter&gt;,p=&lt;par&gt;,keyid=&lt;ver&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;</c>.
/// <c>keyid</c> records the Argon2 keyed-secret version (a Freeboard extension to the
/// standard parameter list); the salt and hash use standard PHC unpadded base64.
/// </remarks>
internal readonly record struct PhcHash(
    int MemoryKiB, int Iterations, int Parallelism, int SecretVersion, byte[] Salt, byte[] Hash)
{
    private const int Argon2Version = 19;

    // Sane bounds so a corrupt/hostile hash row cannot drive an absurd memory/time cost or
    // a mis-sized salt/hash. NumberStyles.None already rejects negatives.
    private const int MinMemoryKiB = 8 * 1024;        // 8 MiB
    private const int MaxMemoryKiB = 1024 * 1024;     // 1 GiB
    private const int MinIterations = 1;
    private const int MaxIterations = 10;
    private const int MinParallelism = 1;
    private const int MaxParallelism = 16;
    private const int ExpectedSaltLength = 16;
    private const int ExpectedHashLength = 32;

    public static string Format(int memoryKiB, int iterations, int parallelism, int secretVersion, byte[] salt, byte[] hash)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"$argon2id$v={Argon2Version}$m={memoryKiB},t={iterations},p={parallelism},keyid={secretVersion}${B64Encode(salt)}${B64Encode(hash)}");

    public static bool TryParse(string? encoded, out PhcHash result)
    {
        result = default;
        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        // Leading empty segment from the leading '$'.
        var parts = encoded.Split('$');
        if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != "argon2id")
        {
            return false;
        }

        if (!parts[2].StartsWith("v=", StringComparison.Ordinal)
            || !int.TryParse(parts[2].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version != Argon2Version)
        {
            return false;
        }

        int? mem = null, iter = null, par = null, keyid = null;
        foreach (var token in parts[3].Split(','))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }

            var key = token[..eq];
            if (!int.TryParse(token.AsSpan(eq + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            switch (key)
            {
                case "m": mem = value; break;
                case "t": iter = value; break;
                case "p": par = value; break;
                case "keyid": keyid = value; break;
                default: return false;
            }
        }

        if (mem is null || iter is null || par is null || keyid is null)
        {
            return false;
        }

        if (mem.Value < MinMemoryKiB || mem.Value > MaxMemoryKiB
            || iter.Value < MinIterations || iter.Value > MaxIterations
            || par.Value < MinParallelism || par.Value > MaxParallelism)
        {
            return false;
        }

        if (!TryB64Decode(parts[4], out var salt) || !TryB64Decode(parts[5], out var hash))
        {
            return false;
        }

        if (salt.Length != ExpectedSaltLength || hash.Length != ExpectedHashLength)
        {
            return false;
        }

        result = new PhcHash(mem.Value, iter.Value, par.Value, keyid.Value, salt, hash);
        return true;
    }

    private static string B64Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');

    private static bool TryB64Decode(string value, out byte[] data)
    {
        data = [];
        var padded = (value.Length % 4) switch
        {
            2 => value + "==",
            3 => value + "=",
            0 => value,
            _ => null,
        };
        if (padded is null)
        {
            return false;
        }

        try
        {
            data = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
