using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Freeboard.TagHelpers;

/// <summary>
/// Renders a rectangular tag in one tint tone, distinct from the pill badge. The tone maps to a
/// single tint class, so red is reachable only through <see cref="MarkTone.Fail"/>.
/// Usage: <c>&lt;fb-tag tone="Brand"&gt;SOC 2&lt;/fb-tag&gt;</c>.
/// </summary>
[HtmlTargetElement("fb-tag")]
public sealed class TagTagHelper : TagHelper
{
    public MarkTone Tone { get; set; } = MarkTone.Neutral;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var child = await output.GetChildContentAsync().ConfigureAwait(false);

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", ToneClass(Tone));
        output.Content.SetHtmlContent(child);
    }

    internal static string ToneClass(MarkTone tone) => tone switch
    {
        MarkTone.Neutral => "fb-tag",
        MarkTone.Brand => "fb-tag fb-tag--brand",
        MarkTone.Ok => "fb-tag fb-tag--ok",
        MarkTone.Warn => "fb-tag fb-tag--warn",
        MarkTone.Fail => "fb-tag fb-tag--fail",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };
}
