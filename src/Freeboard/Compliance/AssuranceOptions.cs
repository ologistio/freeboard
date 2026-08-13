namespace Freeboard.Compliance;

/// <summary>
/// Vendor assurance options bound from the <c>Freeboard:Assurance</c> config section.
/// </summary>
public sealed class AssuranceOptions
{
    public const string SectionName = "Freeboard:Assurance";

    /// <summary>
    /// How many days before an expiry a certification starts reading as expiring. It applies to every
    /// entry that authors no <c>warn_days</c> of its own; an entry that does overrides it.
    /// <para>
    /// Ninety days because an annual certification needs about a quarter's notice to book a renewal audit
    /// and get a report issued, so a shorter window warns after the outcome is already decided.
    /// </para>
    /// <para>
    /// Zero means no advance warning at all: an entry reads valid up to and including its expiry date and
    /// expired the day after. A negative value binds without complaint and behaves the same way, which is
    /// the safe direction - an operator loses notice rather than gaining a false one.
    /// </para>
    /// </summary>
    public int WarnWindowDays { get; set; } = 90;
}
