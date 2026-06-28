using Freeboard.Persistence.Auth;

namespace Freeboard.Persistence.Tests.Auth;

public sealed class WebAuthnSignCounterTests
{
    [Theory]
    [InlineData(0, 0)]   // synced passkey reports and keeps 0
    [InlineData(5, 0)]   // a stored positive counter, presented 0: still accepted
    public void PresentedZeroIsAlwaysAccepted(long stored, long presented)
        => Assert.True(WebAuthnSignCounter.IsAcceptable(stored, presented));

    [Theory]
    [InlineData(5, 6)]   // strictly increasing
    [InlineData(0, 1)]   // stored 0 (synced), presented positive
    [InlineData(0, 99)]
    public void IncreaseIsAccepted(long stored, long presented)
        => Assert.True(WebAuthnSignCounter.IsAcceptable(stored, presented));

    [Theory]
    [InlineData(5, 5)]   // equal, both positive: regression
    [InlineData(5, 4)]   // strictly lower, both positive: regression (cloned authenticator)
    [InlineData(10, 1)]
    public void PositiveRegressionIsRejected(long stored, long presented)
        => Assert.False(WebAuthnSignCounter.IsAcceptable(stored, presented));
}
