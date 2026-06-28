using System.Text;
using Freeboard.Persistence.Auth;

namespace Freeboard.Persistence.Tests.Auth;

public sealed class Argon2idPasswordHasherTests
{
    // Secrets must be at least 32 bytes, so the test peppers are padded to length.
    private static byte[] Secret(string seed) => Encoding.UTF8.GetBytes(seed.PadRight(32, '.'));

    private static AuthCryptoOptions Options(
        int currentSecretVersion = 1, IReadOnlyDictionary<int, byte[]>? secrets = null)
        => new()
        {
            PasswordSecrets = secrets ?? new Dictionary<int, byte[]>
            {
                [1] = Secret("pepper-v1-out-of-band-secret"),
                [2] = Secret("pepper-v2-rotated-secret"),
            },
            CurrentPasswordSecretVersion = currentSecretVersion,
            TokenKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentTokenKeyVersion = 1,
            SecretProtectionKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentSecretProtectionKeyVersion = 1,
        };

    [Fact]
    public void HashThenVerifyRoundTrips()
    {
        var hasher = new Argon2idPasswordHasher(Options());

        var encoded = hasher.Hash("correct horse battery staple");

        Assert.True(hasher.Verify("correct horse battery staple", encoded));
    }

    [Fact]
    public void EachHashUsesADistinctSalt()
    {
        var hasher = new Argon2idPasswordHasher(Options());

        var a = hasher.Hash("same-password");
        var b = hasher.Hash("same-password");

        Assert.NotEqual(a, b);
        Assert.True(hasher.Verify("same-password", a));
        Assert.True(hasher.Verify("same-password", b));
    }

    [Fact]
    public void VerifyFailsOnWrongPassword()
    {
        var hasher = new Argon2idPasswordHasher(Options());
        var encoded = hasher.Hash("right");

        Assert.False(hasher.Verify("wrong", encoded));
    }

    [Fact]
    public void VerifyFailsWhenTheKeyedSecretIsWrong()
    {
        // A hash made under secret v1 cannot verify if v1 is replaced by a different
        // secret: the pepper is part of the memory-hard computation, not appended.
        var encoded = new Argon2idPasswordHasher(Options()).Hash("pw");

        var tampered = new Argon2idPasswordHasher(Options(secrets: new Dictionary<int, byte[]>
        {
            [1] = Secret("a-completely-different-v1-secret"),
        }));

        Assert.False(tampered.Verify("pw", encoded));
    }

    [Fact]
    public void VerifyReturnsFalseForMalformedHash()
    {
        var hasher = new Argon2idPasswordHasher(Options());

        Assert.False(hasher.Verify("pw", "not-a-phc-string"));
        Assert.False(hasher.Verify("pw", string.Empty));
    }

    [Fact]
    public void NeedsRehashIsFalseForCurrentParametersAndSecret()
    {
        var hasher = new Argon2idPasswordHasher(Options(currentSecretVersion: 2));

        var encoded = hasher.Hash("pw");

        Assert.False(hasher.NeedsRehash(encoded));
    }

    [Fact]
    public void NeedsRehashIsTrueWhenSecretVersionIsStale()
    {
        // Produced under v1; current is v2.
        var encoded = new Argon2idPasswordHasher(Options(currentSecretVersion: 1)).Hash("pw");
        var current = new Argon2idPasswordHasher(Options(currentSecretVersion: 2));

        Assert.True(current.NeedsRehash(encoded));
        // It still verifies because v1 remains a retained secret.
        Assert.True(current.Verify("pw", encoded));
    }

    [Fact]
    public void VerifyDecoyAlwaysReturnsFalse()
    {
        var hasher = new Argon2idPasswordHasher(Options());

        Assert.False(hasher.VerifyDecoy("anything"));
        Assert.False(hasher.VerifyDecoy(string.Empty));
    }

    [Fact]
    public void ConstructorThrowsWhenNoSecretsConfigured()
    {
        Assert.Throws<InvalidOperationException>(() => new Argon2idPasswordHasher(new AuthCryptoOptions
        {
            PasswordSecrets = new Dictionary<int, byte[]>(),
            CurrentPasswordSecretVersion = 1,
            TokenKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentTokenKeyVersion = 1,
            SecretProtectionKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentSecretProtectionKeyVersion = 1,
        }));
    }

    // An empty password must never produce or match a stored credential.
    [Fact]
    public void HashThrowsOnEmptyPassword()
    {
        var hasher = new Argon2idPasswordHasher(Options());

        Assert.Throws<ArgumentException>(() => hasher.Hash(string.Empty));
    }

    [Fact]
    public void VerifyEmptyPasswordIsAlwaysFalse()
    {
        var hasher = new Argon2idPasswordHasher(Options());
        var realHash = hasher.Hash("a-real-password");

        Assert.False(hasher.Verify(string.Empty, realHash));
        Assert.False(hasher.Verify(string.Empty, "not-a-phc-string"));
    }

    // Weak crypto material is rejected at construction.
    [Theory]
    [InlineData(0)]   // empty
    [InlineData(16)]  // too short
    [InlineData(31)]  // one byte under the 32-byte minimum
    public void ConstructorThrowsOnWeakSecret(int secretBytes)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new Argon2idPasswordHasher(new AuthCryptoOptions
        {
            PasswordSecrets = new Dictionary<int, byte[]> { [1] = new byte[secretBytes] },
            CurrentPasswordSecretVersion = 1,
            TokenKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentTokenKeyVersion = 1,
            SecretProtectionKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentSecretProtectionKeyVersion = 1,
        }));

        Assert.Contains("version 1", ex.Message, StringComparison.Ordinal);
    }

    // A corrupt PHC string with out-of-range parameters or wrong lengths verifies false,
    // never throws.
    [Theory]
    [InlineData("$argon2id$v=19$m=1024,t=2,p=1,keyid=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // memory below minimum
    [InlineData("$argon2id$v=19$m=19456,t=99,p=1,keyid=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // iterations out of range
    [InlineData("$argon2id$v=19$m=19456,t=2,p=99,keyid=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // parallelism out of range
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1,keyid=1$AAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // short salt
    public void VerifyReturnsFalseForOutOfRangeOrCorruptPhc(string corrupt)
    {
        var hasher = new Argon2idPasswordHasher(Options());

        Assert.False(hasher.Verify("pw", corrupt));
        Assert.True(hasher.NeedsRehash(corrupt));
    }
}
