using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Freeboard.TagHelpers;

/// <summary>
/// Renders an owner as a decorative initials avatar plus the name. The avatar is aria-hidden, so the
/// name carries the meaning. Usage: <c>&lt;fb-owner name="Jane Smith" /&gt;</c>.
/// </summary>
[HtmlTargetElement("fb-owner", TagStructure = TagStructure.WithoutEndTag)]
public sealed class OwnerTagHelper : TagHelper
{
    public string Name { get; set; } = "";

    /// <summary>Override the derived initials where the first two words are not the right pick.</summary>
    public string? Initials { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "fb-owner");

        var content = output.Content;
        content.SetHtmlContent("<span class=\"fb-av\" aria-hidden=\"true\">");
        content.Append(Initials ?? DeriveInitials(Name));
        content.AppendHtml("</span>");
        content.AppendHtml("<span class=\"fb-owner__name\">");
        content.Append(Name);
        content.AppendHtml("</span>");
    }

    internal static string DeriveInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "";
        var first = char.ToUpperInvariant(parts[0][0]);
        if (parts.Length == 1) return first.ToString();
        var last = char.ToUpperInvariant(parts[^1][0]);
        return $"{first}{last}";
    }
}
