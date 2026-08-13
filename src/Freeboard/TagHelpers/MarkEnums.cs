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

/// <summary>
/// The tone for the provenance stamp (<c>fb-stamp</c>), drawn from its own enum rather than
/// <see cref="MarkTone"/>: a stamp has no pass tone and no brand tone, so a green or brand stamp stays
/// unrepresentable. Green would claim a Freeboard verdict the mark does not hold, and the brand-inked
/// <c>gen</c> variant names where a value came from rather than a tone.
/// <para>
/// Each member is named for the colour it emits. <see cref="Neutral"/> is the bare <c>.fb-stamp</c>, the
/// mark's untinted muted ink, NOT the violet <c>gen</c> variant. <c>manual</c> and <c>gen</c> are
/// provenance variants selected by the stamp's own provenance flag, not tones.
/// </para>
/// </summary>
public enum StampTone
{
    Neutral,
    Warn,
    Fail,
}
