namespace Freeboard.TagHelpers;

/// <summary>
/// The complete product-wide status vocabulary, one canonical member per status. Every status a
/// mark can show is here and nothing outside it is representable, so a page cannot invent a status.
/// Ready is a synonym of <see cref="Passing"/> and Degraded of <see cref="Drifting"/>; each synonym
/// pair is one status, not two.
/// </summary>
public enum StatusKind
{
    Passing,
    Failing,
    DueSoon,
    Overdue,
    Drifting,
    Snoozed,
    Waiting,
    Draft,
    OutOfScope,
}

/// <summary>
/// The tint tone for the generic mark helpers (<c>fb-badge</c>, <c>fb-tag</c>). Each member maps to
/// exactly one emitted tint class, so red is reachable only through <see cref="Fail"/>.
/// </summary>
public enum MarkTone
{
    Neutral,
    Brand,
    Ok,
    Warn,
    Fail,
}
