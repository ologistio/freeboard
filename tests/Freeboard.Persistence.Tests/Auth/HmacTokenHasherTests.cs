using System.Security.Cryptography;
using Freeboard.Persistence.Auth;

namespace Freeboard.Persistence.Tests.Auth;

public sealed class HmacTokenHasherTests
{
    // A fixed two-version key set shared across instances so rotation can be tested by
    // changing only the current key version, not the key material.
    private static readonly byte[] Key1 = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Key2 = RandomNumberGenerator.GetBytes(32);

    private static AuthCryptoOptions Options(int currentTokenKeyVersion)
        => new()
        {
            PasswordSecrets = new Dictionary<int, byte[]> { [1] = new byte[16] },
            CurrentPasswordSecretVersion = 1,
            TokenKeys = new Dictionary<int, byte[]> { [1] = Key1, [2] = Key2 },
            CurrentTokenKeyVersion = currentTokenKeyVersion,
            SecretProtectionKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentSecretProtectionKeyVersion = 1,
        };

    [Fact]
    public void MintedPrefixedTokenCarriesCurrentKeyIdAndIsVerifiable()
    {
        var hasher = new HmacTokenHasher(Options(2));

        var minted = hasher.MintPrefixed();

        Assert.StartsWith("v2.", minted.Token);
        Assert.Equal(2, minted.KeyVersion);
        Assert.Equal(32, minted.Hash.Length);

        Assert.True(hasher.TryHashPrefixed(minted.Token, out var hash, out var keyVersion));
        Assert.Equal(2, keyVersion);
        Assert.True(CryptographicOperations.FixedTimeEquals(hash, minted.Hash));
    }

    [Fact]
    public void PrefixedTokenMintedUnderV1StillVerifiesAfterRotationToV2()
    {
        // Key id is parsed from the token, so a v1 token keeps validating once the current
        // key advances to v2.
        var minted = new HmacTokenHasher(Options(1)).MintPrefixed();
        var rotated = new HmacTokenHasher(Options(2));

        Assert.True(rotated.TryHashPrefixed(minted.Token, out var hash, out var keyVersion));
        Assert.Equal(1, keyVersion);
        Assert.True(CryptographicOperations.FixedTimeEquals(hash, minted.Hash));
    }

    // A valid 32-byte base64url secret (43 chars, no padding) for prefix-shape tests.
    private const string ValidSecret = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void PrefixKeyIdSelectsTheCorrectKey()
    {
        // v1 and v2 over the same token bytes must HMAC to different digests, proving the
        // parsed key id selects the key.
        var hasher = new HmacTokenHasher(Options(1));
        Assert.True(hasher.TryHashPrefixed($"v1.{ValidSecret}", out var h1, out _));
        Assert.True(hasher.TryHashPrefixed($"v2.{ValidSecret}", out var h2, out _));

        Assert.False(CryptographicOperations.FixedTimeEquals(h1, h2));
    }

    [Fact]
    public void MalformedOrUnknownKeyPrefixedTokensAreRejectedWithoutAHash()
    {
        var hasher = new HmacTokenHasher(Options(1));

        Assert.False(hasher.TryHashPrefixed("garbage", out _, out _));
        Assert.False(hasher.TryHashPrefixed(string.Empty, out _, out _));
        Assert.False(hasher.TryHashPrefixed("v1", out _, out _));
        Assert.False(hasher.TryHashPrefixed("v1.", out _, out _));
        Assert.False(hasher.TryHashPrefixed(".secret", out _, out _));
        // Unknown key id (no v999 in the key set): rejected with no hash, no DB lookup.
        Assert.False(hasher.TryHashPrefixed($"v999.{ValidSecret}", out _, out _));
    }

    // The secret after v<id>. must be exactly a 32-byte base64url value with no illegal
    // chars, no padding, no extra dots, and the right length - rejected without an HMAC.
    [Fact]
    public void PrefixedTokenWithShortSecretIsRejected()
    {
        var hasher = new HmacTokenHasher(Options(1));

        // The old fixture "c2VjcmV0" is a 6-byte secret and must now be rejected.
        Assert.False(hasher.TryHashPrefixed("v1.c2VjcmV0", out _, out _));
    }

    [Theory]
    [InlineData("v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // 42 chars (too short)
    [InlineData("v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]  // 44 chars (too long)
    [InlineData("v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]   // padding present
    [InlineData("v1.+AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // standard-base64 '+' is illegal
    [InlineData("v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/A")]  // '/' is illegal
    [InlineData("v1.AAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAA")]  // extra dot
    public void PrefixedTokenWithMalformedSecretIsRejected(string token)
    {
        var hasher = new HmacTokenHasher(Options(1));

        Assert.False(hasher.TryHashPrefixed(token, out _, out _));
    }

    [Fact]
    public void PrefixlessTokenVerifiesUnderTheStoredKeyVersionAfterRotation()
    {
        // Minted under current key id 1 (no v<id>. prefix), then verified under the
        // explicitly-stored version after the current key advances to 2.
        var minted = new HmacTokenHasher(Options(1)).MintPrefixless();
        Assert.Equal(1, minted.KeyVersion);
        Assert.DoesNotContain('.', minted.Token);

        var rotated = new HmacTokenHasher(Options(2));

        Assert.True(rotated.TryHashPrefixless(minted.Token, minted.KeyVersion, out var hash));
        Assert.True(CryptographicOperations.FixedTimeEquals(hash, minted.Hash));
    }

    [Fact]
    public void HashPrefixlessIsDeterministicPerKey()
    {
        var hasher = new HmacTokenHasher(Options(1));

        var a = hasher.HashPrefixless("ABCD-EFGH-IJKL");
        var b = hasher.HashPrefixless("ABCD-EFGH-IJKL");

        Assert.True(CryptographicOperations.FixedTimeEquals(a.Hash, b.Hash));
        Assert.Equal("ABCD-EFGH-IJKL", a.Token);
    }

    [Fact]
    public void PrefixlessVerifyFailsForUnknownStoredVersion()
    {
        var hasher = new HmacTokenHasher(Options(1));

        Assert.False(hasher.TryHashPrefixless("code", 999, out _));
    }

    [Fact]
    public void ConstructorThrowsWhenNoKeysConfigured()
    {
        Assert.Throws<InvalidOperationException>(() => new HmacTokenHasher(new AuthCryptoOptions
        {
            PasswordSecrets = new Dictionary<int, byte[]> { [1] = new byte[16] },
            CurrentPasswordSecretVersion = 1,
            TokenKeys = new Dictionary<int, byte[]>(),
            CurrentTokenKeyVersion = 1,
            SecretProtectionKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentSecretProtectionKeyVersion = 1,
        }));
    }

    // A token key shorter than 32 bytes is rejected at construction.
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    public void ConstructorThrowsOnWeakTokenKey(int keyBytes)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new HmacTokenHasher(new AuthCryptoOptions
        {
            PasswordSecrets = new Dictionary<int, byte[]> { [1] = new byte[16] },
            CurrentPasswordSecretVersion = 1,
            TokenKeys = new Dictionary<int, byte[]> { [1] = new byte[keyBytes] },
            CurrentTokenKeyVersion = 1,
            SecretProtectionKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentSecretProtectionKeyVersion = 1,
        }));

        Assert.Contains("version 1", ex.Message, StringComparison.Ordinal);
    }

    // MintPrefixless enforces a 32-byte minimum.
    [Fact]
    public void MintPrefixlessThrowsBelow32Bytes()
    {
        var hasher = new HmacTokenHasher(Options(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => hasher.MintPrefixless(16));
    }

    // Constant-time verify helpers compute and compare internally.
    [Fact]
    public void VerifyPrefixedMatchesTheStoredHashAndRejectsTampering()
    {
        var hasher = new HmacTokenHasher(Options(1));
        var minted = hasher.MintPrefixed();

        Assert.True(hasher.VerifyPrefixed(minted.Token, minted.Hash));
        Assert.False(hasher.VerifyPrefixed(minted.Token, new byte[32]));
        // Malformed token: no comparison, false.
        Assert.False(hasher.VerifyPrefixed("garbage", minted.Hash));
    }

    [Fact]
    public void VerifyPrefixlessMatchesUnderTheStoredVersion()
    {
        var hasher = new HmacTokenHasher(Options(1));
        var minted = hasher.MintPrefixless();

        Assert.True(hasher.VerifyPrefixless(minted.Token, minted.KeyVersion, minted.Hash));
        Assert.False(hasher.VerifyPrefixless(minted.Token, minted.KeyVersion, new byte[32]));
        Assert.False(hasher.VerifyPrefixless(minted.Token, 999, minted.Hash));
    }
}
