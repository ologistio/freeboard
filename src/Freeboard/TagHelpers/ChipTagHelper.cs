using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Freeboard.TagHelpers;

/// <summary>
/// Renders a filter chip with a label, a count, and a selected state (L2). The count is part of the
/// accessible name so a screen reader hears "Failing, 12". Usage:
/// <c>&lt;fb-chip label="Failing" count="12" selected="true" /&gt;</c>.
/// </summary>
[HtmlTargetElement("fb-chip", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ChipTagHelper : TagHelper
{
    public string Label { get; set; } = "";

    public int? Count { get; set; }

    public bool Selected { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        // The label is the chip's accessible name; a blank one leaves a screen reader with nothing
        // (or just a bare count), so reject it rather than emit a nameless control.
        if (string.IsNullOrWhiteSpace(Label))
            throw new InvalidOperationException("An <fb-chip> requires a non-blank 'label'.");

        // L2 requires a filter chip to show its count without opening a menu, so a count is
        // mandatory: reject a chip that omits it rather than render a countless filter.
        if (Count is not { } count)
            throw new InvalidOperationException("An <fb-chip> requires a 'count'.");

        // A count is a cardinality; a negative value would render nonsense like "Failing, -1".
        if (count < 0)
            throw new InvalidOperationException("An <fb-chip> 'count' cannot be negative.");

        output.TagName = "button";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("type", "button");
        output.Attributes.SetAttribute("class", Selected ? "fb-chip on" : "fb-chip");
        output.Attributes.SetAttribute("aria-pressed", Selected ? "true" : "false");
        output.Attributes.SetAttribute("aria-label", $"{Label}, {count}");

        var content = output.Content;
        content.SetContent(Label);
        content.AppendHtml("<span class=\"n\" aria-hidden=\"true\">");
        content.Append(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        content.AppendHtml("</span>");
    }
}
