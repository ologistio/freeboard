using Freeboard.TagHelpers;

namespace Freeboard.Pages.Shared;

/// <summary>
/// One row inside an anatomy section (a relation, an evidence item, or a history entry). Its
/// <see cref="Text"/> is the primary label, optionally followed by trailing marks. A <see cref="Tag"/>
/// row renders the text inside an <c>fb-tag</c> tint (a relation link); a <see cref="Status"/> row
/// appends an <c>fb-status</c> seal-and-word (a proving-check result); a <see cref="Note"/> row appends
/// a plain mono note (an honest "not collected" or a date). A <see cref="Status"/> and a
/// <see cref="Note"/> may co-occur - a stale check shows the degraded seal and names the stopped source.
/// None set renders the text alone.
/// </summary>
public sealed record ObjectDetailRow(
    string Text,
    StatusKind? Status = null,
    MarkTone? Tag = null,
    string? Note = null);

/// <summary>
/// A labelled group of rows within the anatomy body (for example "Satisfies" or "Proving checks"). An
/// empty <see cref="Rows"/> renders an explicit O2 empty under the label rather than being omitted.
/// <see cref="AsTags"/> lays the rows out as a wrapping tag cloud instead of a divided list.
/// </summary>
public sealed record ObjectDetailSection(
    string Label,
    IReadOnlyList<ObjectDetailRow> Rows,
    bool AsTags = false);

/// <summary>
/// The uniform object anatomy (O3), general across object types so every list projects into the one
/// shared partial. The fixed render order is: eyebrow and title, status, then the body sections
/// assertion, relations, evidence, guidance, and history, closing with a context-supplied actions slot.
/// A null or empty facet renders as an explicit O2 empty, never a fabricated value. The control-level
/// <see cref="Status"/> is deliberately nullable: a projection that carries no evaluated status leaves
/// it null so the status facet reads "not evaluated" (S6), never a synthesised pass.
/// </summary>
public sealed record ObjectDetailView(
    string? Eyebrow,
    string Title,
    StatusKind? Status,
    string? Assertion,
    IReadOnlyList<ObjectDetailSection> Relations,
    IReadOnlyList<ObjectDetailRow> Evidence,
    string? Guidance,
    IReadOnlyList<ObjectDetailRow> History);

/// <summary>
/// The partial's model: the anatomy plus the context-dependent actions slot. The actions slot is the one
/// facet that legitimately differs between the drawer and the full page (the drawer offers an "Open full
/// page" link; the full page offers no self-link), so it is excluded from the O3/O4 parity guarantee. It
/// is a link only - this surface is read-only and carries no mutating affordance.
///
/// <see cref="TitleAsPageHeading"/> sets the title's heading level: the full page is a document whose
/// title is its sole <c>h1</c>, while the drawer is a dialog whose title is an <c>h2</c> the
/// <c>aria-labelledby</c> points at. The rendered anatomy is otherwise identical, so O3/O4 parity holds.
/// </summary>
public sealed record ObjectDetailPartialModel(
    ObjectDetailView Detail,
    string? ActionHref = null,
    string? ActionLabel = null,
    bool TitleAsPageHeading = false);
