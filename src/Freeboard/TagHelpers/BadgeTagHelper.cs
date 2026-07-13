using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Freeboard.TagHelpers;

/// <summary>
/// Renders a pill badge in one tint tone. The tone maps to a single tint class, so red is reachable
/// only through <see cref="MarkTone.Fail"/>. Usage: <c>&lt;fb-badge tone="Ok"&gt;Ready&lt;/fb-badge&gt;</c>.
/// </summary>
[HtmlTargetElement("fb-badge")]
public sealed class BadgeTagHelper : TagHelper
{
    public MarkTone Tone { get; set; } = MarkTone.Neutral;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var child = await output.GetChildContentAsync().ConfigureAwait(false);

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", $"badge {ToneClass(Tone)}");
        output.Content.SetHtmlContent(child);
    }

    internal static string ToneClass(MarkTone tone) => tone switch
    {
        MarkTone.Neutral => "badge-neutral",
        MarkTone.Brand => "badge-brand",
        MarkTone.Ok => "badge-success",
        MarkTone.Warn => "badge-warn",
        MarkTone.Fail => "badge-danger",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };
}
