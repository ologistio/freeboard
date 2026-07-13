using System.Globalization;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Freeboard.TagHelpers;

/// <summary>
/// Renders a due date: relative when near, absolute when far, and overdue stated in words (T6).
/// Usage: <c>&lt;fb-due due="@item.DueAt" /&gt;</c>. <see cref="Now"/> is injectable so the relative
/// wording is testable.
/// </summary>
[HtmlTargetElement("fb-due", TagStructure = TagStructure.WithoutEndTag)]
public sealed class DueTagHelper : TagHelper
{
    /// <summary>The due instant. Required: there is no safe default date, so a missing one throws
    /// rather than rendering from <see cref="DateTimeOffset.MinValue"/>.</summary>
    public DateTimeOffset? Due { get; set; }

    /// <summary>Reference instant; defaults to now. Set in tests for deterministic wording.</summary>
    public DateTimeOffset? Now { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var due = Due ?? throw new InvalidOperationException("<fb-due> requires a 'due' attribute.");
        var (text, modifier) = Describe(due, Now ?? DateTimeOffset.Now);

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", modifier is null ? "fb-due" : $"fb-due {modifier}");
        output.Content.SetContent(text);
    }

    // Returns the display text and the state modifier class (null | "soon" | "over").
    internal static (string Text, string? Modifier) Describe(DateTimeOffset due, DateTimeOffset now)
    {
        // Normalize both to UTC before taking the calendar date: due and now can carry different
        // offsets (Now defaults to local), and subtracting their raw .Date can shift the
        // overdue/soon boundary by a day.
        var days = (due.UtcDateTime.Date - now.UtcDateTime.Date).Days;
        if (days < 0)
        {
            var n = -days;
            // T6: far dates are absolute, near ones relative - symmetric to the future side. A long-
            // overdue item still states that it is overdue, just with the absolute date.
            if (n > 7)
                return ($"Overdue since {due.ToString("MMM d", CultureInfo.InvariantCulture)}", "over");
            return (n == 1 ? "1 day overdue" : $"{n} days overdue", "over");
        }
        if (days == 0) return ("Today", "soon");
        if (days == 1) return ("Tomorrow", "soon");
        if (days <= 3) return ($"In {days} days", "soon");
        if (days <= 7) return ($"In {days} days", null);
        return (due.ToString("MMM d", CultureInfo.InvariantCulture), null);
    }
}
