using OtpNet;

namespace Freeboard.Persistence.Tests.Auth;

/// <summary>
/// Pins the Otp.NET compute/verify behaviour and the +/-1 window the TOTP store uses,
/// without a database. The store's atomic replay guard (advance last_time_step) is verified
/// against the database elsewhere; here we confirm the matched time step is what the guard keys on.
/// </summary>
public sealed class TotpComputeTests
{
    private static readonly byte[] Secret = KeyGeneration.GenerateRandomKey(20);

    [Fact]
    public void CurrentCodeVerifiesAndYieldsItsTimeStep()
    {
        var totp = new Totp(Secret);
        var code = totp.ComputeTotp();

        var ok = totp.VerifyTotp(code, out var matchedStep, new VerificationWindow(previous: 1, future: 1));

        Assert.True(ok);
        // The matched step is the absolute 30s step used as the replay watermark.
        Assert.True(matchedStep > 0);
    }

    [Fact]
    public void CodeFromTwoStepsAgoIsOutsideThePlusMinusOneWindow()
    {
        var totp = new Totp(Secret);
        var twoStepsAgo = DateTime.UtcNow.AddSeconds(-60);
        var staleCode = totp.ComputeTotp(twoStepsAgo);

        var ok = totp.VerifyTotp(staleCode, out _, new VerificationWindow(previous: 1, future: 1));

        Assert.False(ok);
    }

    [Fact]
    public void CodeFromOneStepAgoIsInsideTheWindow()
    {
        var totp = new Totp(Secret);
        var oneStepAgo = DateTime.UtcNow.AddSeconds(-30);
        var recentCode = totp.ComputeTotp(oneStepAgo);

        var ok = totp.VerifyTotp(recentCode, out _, new VerificationWindow(previous: 1, future: 1));

        Assert.True(ok);
    }

    [Fact]
    public void WrongCodeDoesNotVerify()
    {
        var totp = new Totp(Secret);

        Assert.False(totp.VerifyTotp("xxxxxx", out _, new VerificationWindow(previous: 1, future: 1)));
    }
}
