using System.Security.Cryptography;
using System.Text;
using Freeboard.Persistence.Auth;

namespace Freeboard.Persistence.Tests.Auth;

public sealed class AesGcmSecretProtectorTests
{
    private static readonly byte[] Key1 = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Key2 = RandomNumberGenerator.GetBytes(32);

    private static AuthCryptoOptions Options(int currentVersion, IReadOnlyDictionary<int, byte[]>? keys = null)
        => new()
        {
            PasswordSecrets = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentPasswordSecretVersion = 1,
            TokenKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentTokenKeyVersion = 1,
            SecretProtectionKeys = keys ?? new Dictionary<int, byte[]> { [1] = Key1, [2] = Key2 },
            CurrentSecretProtectionKeyVersion = currentVersion,
        };

    [Fact]
    public void ProtectThenUnprotectRoundTripsAndStampsCurrentVersion()
    {
        var protector = new AesGcmSecretProtector(Options(2));
        var plaintext = Encoding.UTF8.GetBytes("totp-secret-bytes");

        var sealed_ = protector.Protect(plaintext);

        Assert.Equal(2, sealed_.KeyVersion);
        Assert.Equal(12, sealed_.Nonce.Length);
        Assert.Equal(16, sealed_.Tag.Length);
        Assert.NotEqual(plaintext, sealed_.Ciphertext);
        Assert.Equal(plaintext, protector.Unprotect(sealed_));
    }

    [Fact]
    public void EachProtectUsesAFreshNonce()
    {
        var protector = new AesGcmSecretProtector(Options(1));
        var plaintext = Encoding.UTF8.GetBytes("same-input");

        var a = protector.Protect(plaintext);
        var b = protector.Protect(plaintext);

        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEqual(a.Ciphertext, b.Ciphertext);
    }

    [Fact]
    public void TamperedCiphertextFailsAuthentication()
    {
        var protector = new AesGcmSecretProtector(Options(1));
        var sealed_ = protector.Protect(Encoding.UTF8.GetBytes("totp-secret"));
        sealed_.Ciphertext[0] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(sealed_));
    }

    [Fact]
    public void TamperedTagFailsAuthentication()
    {
        var protector = new AesGcmSecretProtector(Options(1));
        var sealed_ = protector.Protect(Encoding.UTF8.GetBytes("totp-secret"));
        sealed_.Tag[0] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(sealed_));
    }

    [Fact]
    public void CiphertextSealedUnderV1StillDecryptsAfterRotationToV2()
    {
        var sealed_ = new AesGcmSecretProtector(Options(1)).Protect(Encoding.UTF8.GetBytes("rotated-secret"));
        Assert.Equal(1, sealed_.KeyVersion);

        var rotated = new AesGcmSecretProtector(Options(2));

        Assert.Equal(Encoding.UTF8.GetBytes("rotated-secret"), rotated.Unprotect(sealed_));
    }

    [Fact]
    public void UnprotectThrowsForUnknownStoredKeyVersion()
    {
        var protector = new AesGcmSecretProtector(Options(1));
        var sealed_ = protector.Protect(Encoding.UTF8.GetBytes("x")) with { KeyVersion = 999 };

        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(sealed_));
    }

    [Fact]
    public void ConstructorThrowsWhenNoKeyConfigured()
    {
        Assert.Throws<InvalidOperationException>(
            () => new AesGcmSecretProtector(Options(1, new Dictionary<int, byte[]>())));
    }

    // AES-256-GCM needs an EXACTLY 32-byte key. Both too-short and too-long keys must fail at
    // construction (and thus at startup), not later inside AesGcm at the first TOTP seal.
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void ConstructorThrowsOnWrongLengthKey(int keyBytes)
    {
        Assert.Throws<InvalidOperationException>(
            () => new AesGcmSecretProtector(Options(1, new Dictionary<int, byte[]> { [1] = new byte[keyBytes] })));
    }
}
