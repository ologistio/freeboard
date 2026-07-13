using System.Text;
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
        // The name carries the mark's meaning (the avatar is aria-hidden); a blank one emits an empty
        // mark, so reject it as the other required mark helpers do.
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("An <fb-owner> requires a non-blank 'name'.");

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
        var first = FirstLetter(parts[0]);
        return parts.Length == 1 ? first : first + FirstLetter(parts[^1]);
    }

    // First letter as a full Unicode scalar, not a UTF-16 code unit, so a name that starts with a
    // non-BMP character (e.g. a supplementary-plane letter) yields a whole glyph, not a lone surrogate.
    private static string FirstLetter(string word)
        => Rune.ToUpperInvariant(Rune.GetRuneAt(word, 0)).ToString();
}
