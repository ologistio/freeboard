using Freeboard.Core.Assets;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the derived assurance status and, by name, the boundaries a bug in it would move silently: the
/// expiry date itself, the last day of the warning window, and the day after an expiry inside a window.
/// Every case supplies its own date, so nothing here depends on the wall clock.
/// </summary>
public sealed class AssuranceStatusTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public void DistantExpiryIsValid()
    {
        Assert.Equal(AssuranceStatus.Valid, VendorAssurance.Evaluate(new DateOnly(2027, 3, 1), 90, Today));
    }

    [Fact]
    public void ExpiryInsideTheWindowIsExpiring()
    {
        Assert.Equal(AssuranceStatus.Expiring, VendorAssurance.Evaluate(new DateOnly(2026, 4, 1), 90, Today));
    }

    [Fact]
    public void PassedExpiryIsExpired()
    {
        Assert.Equal(AssuranceStatus.Expired, VendorAssurance.Evaluate(new DateOnly(2026, 2, 28), 90, Today));
    }

    [Fact]
    public void ExpiryOnTodayIsExpiringWithinAWindowAndValidWithNone()
    {
        Assert.Equal(AssuranceStatus.Expiring, VendorAssurance.Evaluate(Today, 90, Today));
        Assert.Equal(AssuranceStatus.Valid, VendorAssurance.Evaluate(Today, 0, Today));
    }

    [Fact]
    public void LastDayOfTheWindowIsExpiring()
    {
        Assert.Equal(AssuranceStatus.Expiring, VendorAssurance.Evaluate(Today.AddDays(30), 30, Today));
        Assert.Equal(AssuranceStatus.Valid, VendorAssurance.Evaluate(Today.AddDays(31), 30, Today));
    }

    [Fact]
    public void YesterdaysExpiryIsExpiredInsideANonZeroWindow()
    {
        Assert.Equal(AssuranceStatus.Expired, VendorAssurance.Evaluate(Today.AddDays(-1), 30, Today));
    }

    [Fact]
    public void AZeroWindowGivesNoAdvanceNotice()
    {
        Assert.Equal(AssuranceStatus.Valid, VendorAssurance.Evaluate(Today.AddDays(1), 0, Today));
        Assert.Equal(AssuranceStatus.Valid, VendorAssurance.Evaluate(Today, 0, Today));
        Assert.Equal(AssuranceStatus.Expired, VendorAssurance.Evaluate(Today.AddDays(-1), 0, Today));

        // A negative deployment window is not rejected at bind time, so it has to degrade here rather
        // than warn for a nonsensical span.
        Assert.Equal(AssuranceStatus.Valid, VendorAssurance.Evaluate(Today.AddDays(1), -30, Today));
        Assert.Equal(AssuranceStatus.Expired, VendorAssurance.Evaluate(Today.AddDays(-1), -30, Today));
    }

    [Fact]
    public void APerEntryOverridePutsTwoAssurancesSharingAnExpiryInDifferentStates()
    {
        var expires = Today.AddDays(45);

        Assert.Equal(AssuranceStatus.Expiring, VendorAssurance.Evaluate(expires, 90, Today));
        Assert.Equal(AssuranceStatus.Valid, VendorAssurance.Evaluate(expires, 30, Today));
    }

    [Fact]
    public void AnEnormousWindowIsExpiringRatherThanThrowing()
    {
        // Fails if the rule ever goes back to today.AddDays(warnDays), which throws out of range here.
        Assert.Equal(AssuranceStatus.Expiring, VendorAssurance.Evaluate(Today.AddDays(1), int.MaxValue, Today));
    }
}
