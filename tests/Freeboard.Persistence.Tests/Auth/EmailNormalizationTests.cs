using Freeboard.Persistence.Auth;

namespace Freeboard.Persistence.Tests.Auth;

public sealed class EmailNormalizationTests
{
    [Theory]
    [InlineData("User@Example.COM", "user@example.com")]
    [InlineData("  spaced@example.com  ", "spaced@example.com")]
    [InlineData("already@lower.com", "already@lower.com")]
    public void NormalizeTrimsAndLowercasesInvariant(string input, string expected)
        => Assert.Equal(expected, IUserStore.Normalize(input));
}
