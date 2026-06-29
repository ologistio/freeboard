using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// HMAC-SHA256 keyed token hasher over the versioned key set in
/// <see cref="AuthCryptoOptions.TokenKeys"/>. Prefix-bearing tokens use the wire
/// format <c>v&lt;keyId&gt;.&lt;secret-base64url&gt;</c> with a 32-byte secret; prefixless
/// tokens verify under an explicitly-supplied stored key version.
/// </summary>
public sealed class HmacTokenHasher : ITokenHasher
{
    private const int SecretLength = 32;

    private readonly AuthCryptoOptions options;

    public HmacTokenHasher(AuthCryptoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AuthKeyMaterial.Validate(options.TokenKeys, options.CurrentTokenKeyVersion, "AuthCryptoOptions.TokenKeys");
        this.options = options;
    }

    public MintedToken MintPrefixed()
    {
        var secret = RandomNumberGenerator.GetBytes(SecretLength);
        var version = options.CurrentTokenKeyVersion;
        var token = string.Create(CultureInfo.InvariantCulture, $"v{version}.{Base64Url.Encode(secret)}");
        return new MintedToken(token, ComputeHmac(token, version), version);
    }

    public MintedToken MintPrefixless(int secretLength = SecretLength)
    {
        if (secretLength < SecretLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secretLength), secretLength, $"Prefixless secret must be at least {SecretLength} bytes.");
        }

        var secret = Base64Url.Encode(RandomNumberGenerator.GetBytes(secretLength));
        return HashPrefixless(secret);
    }

    public MintedToken HashPrefixless(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var version = options.CurrentTokenKeyVersion;
        return new MintedToken(secret, ComputeHmac(secret, version), version);
    }

    public bool TryHashPrefixed(string token, out byte[] hash, out int keyVersion)
    {
        hash = [];
        keyVersion = 0;
        if (!TryParsePrefixed(token, out var version))
        {
            return false;
        }

        keyVersion = version;
        hash = ComputeHmac(token, version);
        return true;
    }

    public bool TryHashPrefixless(string token, int keyVersion, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrEmpty(token) || !options.TokenKeys.ContainsKey(keyVersion))
        {
            return false;
        }

        hash = ComputeHmac(token, keyVersion);
        return true;
    }

    public bool VerifyPrefixed(string token, ReadOnlySpan<byte> expectedHash)
        => TryHashPrefixed(token, out var hash, out _)
            && CryptographicOperations.FixedTimeEquals(hash, expectedHash);

    public bool VerifyPrefixless(string token, int keyVersion, ReadOnlySpan<byte> expectedHash)
        => TryHashPrefixless(token, keyVersion, out var hash)
            && CryptographicOperations.FixedTimeEquals(hash, expectedHash);

    /// <summary>
    /// Validates the <c>v&lt;keyId&gt;.&lt;secret&gt;</c> shape WITHOUT computing an HMAC or
    /// touching the DB: a known key id, exactly one dot, and a 32-byte base64url
    /// secret with no illegal chars or padding.
    /// </summary>
    private bool TryParsePrefixed(string token, out int keyVersion)
    {
        keyVersion = 0;
        if (string.IsNullOrEmpty(token) || token[0] != 'v')
        {
            return false;
        }

        var dot = token.IndexOf('.');
        if (dot <= 1 || dot == token.Length - 1)
        {
            return false;
        }

        // Exactly one dot: reject a second separator outright.
        if (token.IndexOf('.', dot + 1) >= 0)
        {
            return false;
        }

        if (!int.TryParse(token.AsSpan(1, dot - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || !options.TokenKeys.ContainsKey(version))
        {
            return false;
        }

        if (!IsExactLengthBase64UrlSecret(token.AsSpan(dot + 1)))
        {
            return false;
        }

        keyVersion = version;
        return true;
    }

    private static bool IsExactLengthBase64UrlSecret(ReadOnlySpan<char> secret)
    {
        // 32 bytes unpadded base64url is exactly 43 chars; reject any padding or other length.
        const int ExpectedChars = 43;
        if (secret.Length != ExpectedChars)
        {
            return false;
        }

        Span<char> std = stackalloc char[ExpectedChars + 1];
        for (var i = 0; i < ExpectedChars; i++)
        {
            std[i] = secret[i] switch
            {
                '-' => '+',
                '_' => '/',
                '+' or '/' or '=' => '\0', // standard-base64/padding chars are not valid base64url
                var c => c,
            };
            if (std[i] == '\0')
            {
                return false;
            }
        }

        std[ExpectedChars] = '='; // 43 unpadded chars + one '=' = 44 chars -> a 32-byte decode
        Span<byte> decoded = stackalloc byte[SecretLength];
        return Convert.TryFromBase64Chars(std, decoded, out var written) && written == SecretLength;
    }

    private byte[] ComputeHmac(string token, int keyVersion)
        => HMACSHA256.HashData(options.TokenKeys[keyVersion], Encoding.UTF8.GetBytes(token));
}

/// <summary>Unpadded base64url helper for token secrets.</summary>
internal static class Base64Url
{
    public static string Encode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
