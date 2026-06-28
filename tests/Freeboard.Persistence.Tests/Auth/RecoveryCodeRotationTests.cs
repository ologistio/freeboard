using System.Security.Cryptography;
using Freeboard.Persistence.Auth;

namespace Freeboard.Persistence.Tests.Auth;

/// <summary>
/// Recovery codes are stored prefixless via <see cref="ITokenHasher.HashPrefixless"/> with
/// the minting key version recorded per row, and verified via <see
/// cref="ITokenHasher.VerifyPrefixless"/> under that STORED version. These tests pin the
/// rotation behaviour the store relies on, without a database; the DB round-trip is verified elsewhere.
/// </summary>
public sealed class RecoveryCodeRotationTests
{
    private static readonly byte[] Key1 = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Key2 = RandomNumberGenerator.GetBytes(32);

    private static AuthCryptoOptions Options(int currentTokenKeyVersion)
        => new()
        {
            PasswordSecrets = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentPasswordSecretVersion = 1,
            TokenKeys = new Dictionary<int, byte[]> { [1] = Key1, [2] = Key2 },
            CurrentTokenKeyVersion = currentTokenKeyVersion,
            SecretProtectionKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentSecretProtectionKeyVersion = 1,
        };

    [Fact]
    public void CodeHashedUnderV1StillVerifiesUnderStoredVersionAfterRotationToV2()
    {
        // Mint (hash) a recovery code under current key id 1 and record its key version.
        var minted = new HmacTokenHasher(Options(1)).HashPrefixless("ABCDE-FGHJK");
        Assert.Equal(1, minted.KeyVersion);

        // Current key advances to 2. The stored key version (1) still names a retained key.
        var rotated = new HmacTokenHasher(Options(2));

        Assert.True(rotated.VerifyPrefixless("ABCDE-FGHJK", minted.KeyVersion, minted.Hash));
    }

    [Fact]
    public void WrongCodeDoesNotVerify()
    {
        var minted = new HmacTokenHasher(Options(1)).HashPrefixless("ABCDE-FGHJK");
        var hasher = new HmacTokenHasher(Options(1));

        Assert.False(hasher.VerifyPrefixless("WRONG-CODE0", minted.KeyVersion, minted.Hash));
    }

    [Fact]
    public void VerifyFailsWhenStoredVersionNamesNoRetainedKey()
    {
        var minted = new HmacTokenHasher(Options(1)).HashPrefixless("ABCDE-FGHJK");

        Assert.False(new HmacTokenHasher(Options(1)).VerifyPrefixless("ABCDE-FGHJK", 999, minted.Hash));
    }
}
