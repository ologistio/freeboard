using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Freeboard.TagHelpers;

/// <summary>
/// Renders a provenance stamp. An automated value names its source and age (P1); a manual value is
/// stamped MANUAL and dated (P2). Usage: <c>&lt;fb-stamp source="AWS Config" age="2h ago" /&gt;</c>
/// or <c>&lt;fb-stamp manual age="Mar 3" /&gt;</c>.
/// </summary>
[HtmlTargetElement("fb-stamp", TagStructure = TagStructure.WithoutEndTag)]
public sealed class StampTagHelper : TagHelper
{
    /// <summary>The collecting source for an automated value; required unless <see cref="Manual"/>.</summary>
    public string? Source { get; set; }

    /// <summary>When true, the value is hand-entered: stamped MANUAL rather than named by source.</summary>
    public bool Manual { get; set; }

    /// <summary>Age or date of the value, for example "2h ago" or "Mar 3". Required for both an
    /// automated value ("as of" the age) and a manual value (the date it was entered).</summary>
    public string? Age { get; set; }

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
        output.Attributes.SetAttribute("class", Manual ? "fb-stamp manual" : "fb-stamp gen");

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
