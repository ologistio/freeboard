using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Freeboard.TagHelpers;

/// <summary>
/// Renders a provenance stamp. An automated value names its source and age (P1); a manual value is
/// stamped MANUAL and dated (P2). A value that REFERENCES a named record held outside the system, shown
/// with that record's own date, names the record instead of MANUAL and still carries the date - a
/// vendor's certification names the standard it is against and the date it expires. Content a person
/// supplies stays MANUAL, whatever external thing it describes. Usage:
/// <c>&lt;fb-stamp source="AWS Config" age="2h ago" /&gt;</c>, <c>&lt;fb-stamp manual age="Mar 3" /&gt;</c>,
/// or <c>&lt;fb-stamp source="SOC 2" age="expires Mar 27" tone="Warn" /&gt;</c>.
/// </summary>
[HtmlTargetElement("fb-stamp", TagStructure = TagStructure.WithoutEndTag)]
public sealed class StampTagHelper : TagHelper
{
    /// <summary>
    /// What the displayed value came from: the collecting integration for an automated value, and for a
    /// certification held on file, the standard that certification is against. Required unless
    /// <see cref="Manual"/>.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>When true, the value is hand-entered: stamped MANUAL rather than named by source.</summary>
    public bool Manual { get; set; }

    /// <summary>Age or date of the value, for example "2h ago" or "Mar 3". Required for both an
    /// automated value ("as of" the age) and a manual value (the date it was entered).</summary>
    public string? Age { get; set; }

    /// <summary>
    /// Optional tone. It SELECTS the variant class rather than adding to it, so a toned stamp never
    /// carries a provenance variant's colour underneath its tone. No tone is the default and keeps
    /// today's provenance rendering (<c>manual</c> when <see cref="Manual"/> is set, <c>gen</c>
    /// otherwise), so no existing caller shifts.
    /// </summary>
    public StampTone? Tone { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        // Provenance is the point of the mark: a manual stamp must carry its date, and an automated
        // stamp must name both its source and its age ("as of"), so a missing one throws rather than
        // rendering an empty claim.
        if (Manual)
        {
            if (string.IsNullOrWhiteSpace(Age))
                throw new InvalidOperationException("A manual <fb-stamp> requires an 'age' (the date it was entered).");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Source))
                throw new InvalidOperationException("An automated <fb-stamp> requires a 'source' (or set 'manual').");
            if (string.IsNullOrWhiteSpace(Age))
                throw new InvalidOperationException("An automated <fb-stamp> requires an 'age' (when the value was collected).");
        }

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Tone switch
        {
            StampTone.Neutral => "fb-stamp",
            StampTone.Warn => "fb-stamp warn",
            StampTone.Fail => "fb-stamp fail",
            _ => Manual ? "fb-stamp manual" : "fb-stamp gen",
        });

        var label = Manual ? "MANUAL" : Source!;
        var content = output.Content;
        content.SetContent(label);
        if (!string.IsNullOrWhiteSpace(Age))
        {
            content.AppendHtml("<span class=\"fb-stamp__age\"> ");
            content.Append(Age);
            content.AppendHtml("</span>");
        }
    }
}
