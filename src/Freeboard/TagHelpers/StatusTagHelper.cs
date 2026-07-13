using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Freeboard.TagHelpers;

/// <summary>
/// Renders a status as a decorative seal plus its accessible word (S2). The kind maps to word, tone,
/// and seal in one place, so the word and colour cannot disagree and red stays reserved for failing
/// and overdue (S3). Usage: <c>&lt;fb-status status="DueSoon" /&gt;</c>.
/// </summary>
[HtmlTargetElement("fb-status", TagStructure = TagStructure.WithoutEndTag)]
public sealed class StatusTagHelper : TagHelper
{
    /// <summary>The status to show. Required: a status has no safe default, so a missing one throws
    /// rather than silently rendering the wrong state.</summary>
    public StatusKind? Status { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var status = Status ?? throw new InvalidOperationException("<fb-status> requires a 'status' attribute.");
        var (word, toneClass, sealTone) = Map(status);

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        var cls = toneClass is null ? "fb-status" : $"fb-status {toneClass}";
        output.Attributes.SetAttribute("class", cls);

        var content = output.Content;
        content.SetHtmlContent($"<span class=\"fb-seal {sealTone}\" aria-hidden=\"true\"></span>");
        content.AppendHtml("<span class=\"fb-status__word\">");
        content.Append(word);
        content.AppendHtml("</span>");
    }

    // Word, status text-colour class (null = neutral, no colour), and seal tone class.
    internal static (string Word, string? ToneClass, string SealTone) Map(StatusKind kind) => kind switch
    {
        StatusKind.Passing => ("Passing", "ok", "ok"),
        StatusKind.Failing => ("Failing", "fail", "fail"),
        StatusKind.DueSoon => ("Due soon", "warn", "warn"),
        StatusKind.Overdue => ("Overdue", "fail", "fail"),
        StatusKind.Drifting => ("Drifting", "warn", "warn"),
        StatusKind.Snoozed => ("Snoozed", null, "off"),
        StatusKind.Waiting => ("Waiting", null, "off"),
        StatusKind.Draft => ("Draft", null, "off"),
        StatusKind.OutOfScope => ("Out of scope", null, "off"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
