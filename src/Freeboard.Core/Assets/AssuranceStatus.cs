namespace Freeboard.Core.Assets;

/// <summary>
/// The state of a certification a vendor holds. Derived from the expiry and the clock, never stored and
/// never authored: a stored status is wrong from the moment the clock passes it, and an authored one is a
/// second fact that can contradict the date beside it.
/// </summary>
public enum AssuranceStatus
{
    Valid,
    Expiring,
    Expired,
}

/// <summary>
/// The pure derivation of a vendor assurance's <see cref="AssuranceStatus"/>. Clock-free: the caller supplies the current
/// date, matching <see cref="GitOps.CollectorFrequency.IsStale"/>, so it needs no clock abstraction, no
/// database, and no configuration to be exercised.
/// </summary>
public static class VendorAssurance
{
    /// <summary>
    /// The status of a certification expiring on <paramref name="expires"/>, evaluated at
    /// <paramref name="today"/> against an effective warning window of <paramref name="warnDays"/> days.
    /// A certificate is valid through its expiry date, so only a date past it is expired.
    /// <para>
    /// The window is compared as a difference of day numbers rather than as
    /// <c>expires &lt;= today.AddDays(warnDays)</c>. <paramref name="warnDays"/> arrives as an unbounded
    /// <c>int</c> (the deployment default binds with no validation), and <c>AddDays</c> throws once the sum
    /// leaves the supported date range, which would fault every surface that renders the status. A
    /// difference of two day numbers cannot overflow.
    /// </para>
    /// <para>
    /// The <c>warnDays &gt; 0</c> guard is load-bearing: it is what makes a window of zero mean no advance
    /// notice at all, warning only once the certificate has expired. Without it a zero window would still
    /// warn on the expiry date itself, a day of notice the author asked not to receive. A negative window
    /// behaves exactly as zero, so nothing clamps it.
    /// </para>
    /// </summary>
    public static AssuranceStatus Evaluate(DateOnly expires, int warnDays, DateOnly today)
    {
        if (expires < today)
        {
            return AssuranceStatus.Expired;
        }

        return warnDays > 0 && expires.DayNumber - today.DayNumber <= warnDays
            ? AssuranceStatus.Expiring
            : AssuranceStatus.Valid;
    }
}
