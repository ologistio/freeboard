using System.Text.Encodings.Web;
using Freeboard.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Freeboard.Web.Tests;

/// <summary>
/// Unit tests for the mark tag helpers: correct classes, ARIA, and enum-to-class mapping. The typed
/// enums make illegal states unrepresentable (no status outside S1, red only through Fail), which
/// these tests pin.
/// </summary>
public sealed class MarkTagHelperTests
{
    private static string Render(TagHelper helper, string tagName, string? childHtml = null)
    {
        var context = new TagHelperContext(
            new TagHelperAttributeList(), new Dictionary<object, object>(), Guid.NewGuid().ToString("N"));
        var output = new TagHelperOutput(
            tagName,
            new TagHelperAttributeList(),
            (_, _) =>
            {
                var content = new DefaultTagHelperContent();
                if (childHtml is not null) content.AppendHtml(childHtml);
                return Task.FromResult<TagHelperContent>(content);
            });

        // The base ProcessAsync calls Process, so this drives sync and async helpers alike.
        helper.ProcessAsync(context, output).GetAwaiter().GetResult();

        using var sw = new StringWriter();
        output.WriteTo(sw, HtmlEncoder.Default);
        return sw.ToString();
    }

    [Theory]
    [InlineData(StatusKind.Passing, "Passing", "fb-seal ok", "fb-status ok")]
    [InlineData(StatusKind.Failing, "Failing", "fb-seal fail", "fb-status fail")]
    [InlineData(StatusKind.DueSoon, "Due soon", "fb-seal warn", "fb-status warn")]
    [InlineData(StatusKind.Overdue, "Overdue", "fb-seal fail", "fb-status fail")]
    [InlineData(StatusKind.Drifting, "Drifting", "fb-seal warn", "fb-status warn")]
    [InlineData(StatusKind.Snoozed, "Snoozed", "fb-seal off", "fb-status")]
    [InlineData(StatusKind.Waiting, "Waiting", "fb-seal off", "fb-status")]
    [InlineData(StatusKind.Draft, "Draft", "fb-seal off", "fb-status")]
    [InlineData(StatusKind.OutOfScope, "Out of scope", "fb-seal off", "fb-status")]
    public void StatusMapsKindToWordSealAndAria(StatusKind kind, string word, string sealClass, string statusClass)
    {
        var html = Render(new StatusTagHelper { Status = kind }, "fb-status");

        Assert.Contains($"class=\"{statusClass}\"", html, StringComparison.Ordinal);
        Assert.Contains($"class=\"{sealClass}\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\"", html, StringComparison.Ordinal); // seal is decorative (S2)
        Assert.Contains(word, html, StringComparison.Ordinal); // accessible word carries the meaning
    }

    [Fact]
    public void StatusKindCoversTheWholeS1VocabularyAndRedIsReservedForFailingAndOverdue()
    {
        var kinds = Enum.GetValues<StatusKind>();
        Assert.Equal(9, kinds.Length); // one canonical member per S1 status (Ready/Degraded are aliases)

        foreach (var kind in kinds)
        {
            var (_, tone, seal) = StatusTagHelper.Map(kind);
            var isRed = seal == "fail" || tone == "fail";
            // S3: red only for failing and overdue.
            Assert.Equal(kind is StatusKind.Failing or StatusKind.Overdue, isRed);
        }
    }

    [Theory]
    [InlineData(MarkTone.Neutral, "badge badge-neutral")]
    [InlineData(MarkTone.Brand, "badge badge-brand")]
    [InlineData(MarkTone.Ok, "badge badge-success")]
    [InlineData(MarkTone.Warn, "badge badge-warn")]
    [InlineData(MarkTone.Fail, "badge badge-danger")]
    public void BadgeMapsToneToOneTintClass(MarkTone tone, string expected)
    {
        var html = Render(new BadgeTagHelper { Tone = tone }, "fb-badge", "Ready");
        Assert.Contains($"class=\"{expected}\"", html, StringComparison.Ordinal);
        Assert.Contains("Ready", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MarkTone.Neutral, "fb-tag")]
    [InlineData(MarkTone.Brand, "fb-tag fb-tag--brand")]
    [InlineData(MarkTone.Ok, "fb-tag fb-tag--ok")]
    [InlineData(MarkTone.Warn, "fb-tag fb-tag--warn")]
    [InlineData(MarkTone.Fail, "fb-tag fb-tag--fail")]
    public void TagMapsToneToOneTintClass(MarkTone tone, string expected)
    {
        var html = Render(new TagTagHelper { Tone = tone }, "fb-tag", "SOC 2");
        Assert.Contains($"class=\"{expected}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMarkToneHasAClassAndRedIsReachableOnlyThroughFail()
    {
        var tones = Enum.GetValues<MarkTone>();
        Assert.Equal(5, tones.Length); // no Info; Brand present

        foreach (var tone in tones)
        {
            var badge = BadgeTagHelper.ToneClass(tone);
            var tag = TagTagHelper.ToneClass(tone);
            Assert.False(string.IsNullOrWhiteSpace(badge));
            Assert.False(string.IsNullOrWhiteSpace(tag));
            var red = badge.Contains("danger") || tag.Contains("fail");
            Assert.Equal(tone == MarkTone.Fail, red);
        }
    }

    [Fact]
    public void DueStatesOverdueInWords()
    {
        var now = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        var html = Render(new DueTagHelper { Due = now.AddDays(-3), Now = now }, "fb-due");
        Assert.Contains("class=\"fb-due over\"", html, StringComparison.Ordinal);
        Assert.Contains("3 days overdue", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "Today", "fb-due soon")]
    [InlineData(1, "Tomorrow", "fb-due soon")]
    [InlineData(2, "In 2 days", "fb-due soon")]
    [InlineData(6, "In 6 days", "fb-due")]
    public void DueIsRelativeWhenNear(int days, string text, string cls)
    {
        var now = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        var html = Render(new DueTagHelper { Due = now.AddDays(days), Now = now }, "fb-due");
        Assert.Contains($"class=\"{cls}\"", html, StringComparison.Ordinal);
        Assert.Contains(text, html, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusWithoutAStatusThrows()
    {
        // A missing status must fail loudly, not silently render a default (would defeat S1/S2/S3).
        Assert.Throws<InvalidOperationException>(() => Render(new StatusTagHelper(), "fb-status"));
    }

    [Fact]
    public void DueWithoutADueThrows()
    {
        Assert.Throws<InvalidOperationException>(() => Render(new DueTagHelper(), "fb-due"));
    }

    [Fact]
    public void AutomatedStampWithoutASourceThrows()
    {
        Assert.Throws<InvalidOperationException>(() => Render(new StampTagHelper { Age = "2h ago" }, "fb-stamp"));
    }

    [Fact]
    public void ManualStampWithoutADateThrows()
    {
        Assert.Throws<InvalidOperationException>(() => Render(new StampTagHelper { Manual = true }, "fb-stamp"));
    }

    [Fact]
    public void AutomatedStampWithoutAnAgeThrows()
    {
        // An automated value must name its source and its age ("as of"); a missing age is an empty claim.
        Assert.Throws<InvalidOperationException>(() => Render(new StampTagHelper { Source = "AWS Config" }, "fb-stamp"));
    }

    [Fact]
    public void AutomatedStampWithABlankAgeThrows()
    {
        Assert.Throws<InvalidOperationException>(() => Render(new StampTagHelper { Source = "AWS Config", Age = "   " }, "fb-stamp"));
    }

    [Fact]
    public void AutomatedStampWithABlankSourceThrows()
    {
        // Whitespace is not a source: it would stamp an empty provenance claim.
        Assert.Throws<InvalidOperationException>(() => Render(new StampTagHelper { Source = "   ", Age = "2h ago" }, "fb-stamp"));
    }

    [Fact]
    public void ManualStampWithABlankDateThrows()
    {
        Assert.Throws<InvalidOperationException>(() => Render(new StampTagHelper { Manual = true, Age = "   " }, "fb-stamp"));
    }

    [Fact]
    public void ChipWithoutALabelThrows()
    {
        // A blank label leaves the button with no accessible name (or just a bare count).
        Assert.Throws<InvalidOperationException>(() => Render(new ChipTagHelper { Count = 12 }, "fb-chip"));
        Assert.Throws<InvalidOperationException>(() => Render(new ChipTagHelper { Label = "   ", Count = 12 }, "fb-chip"));
    }

    [Fact]
    public void DueUsesUtcSoDifferingOffsetsDoNotShiftTheDayBoundary()
    {
        // due and now are the same UTC day but carry different offsets; the calendar-day diff must be
        // taken in a common offset (UTC), not from each value's raw local .Date.
        var due = new DateTimeOffset(2026, 3, 11, 2, 0, 0, TimeSpan.FromHours(5)); // 2026-03-10 21:00 UTC
        var now = new DateTimeOffset(2026, 3, 10, 23, 0, 0, TimeSpan.Zero);        // 2026-03-10 23:00 UTC
        var (text, modifier) = DueTagHelper.Describe(due, now);
        Assert.Equal("Today", text);
        Assert.Equal("soon", modifier);
    }

    [Fact]
    public void DueIsAbsoluteWhenFar()
    {
        var now = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        var (text, modifier) = DueTagHelper.Describe(now.AddDays(30), now);
        Assert.Null(modifier);
        Assert.Equal("Apr 9", text);
    }

    [Fact]
    public void ChipCarriesLabelCountAndSelectedState()
    {
        var html = Render(new ChipTagHelper { Label = "Failing", Count = 12, Selected = true }, "fb-chip");
        Assert.Contains("class=\"fb-chip on\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-pressed=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Failing, 12\"", html, StringComparison.Ordinal);
        Assert.Contains(">12<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChipWithoutCountOmitsTheCountSpan()
    {
        var html = Render(new ChipTagHelper { Label = "Owned by me", Selected = false }, "fb-chip");
        Assert.Contains("class=\"fb-chip\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-pressed=\"false\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"n\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnerDerivesInitialsAndHidesTheAvatar()
    {
        var html = Render(new OwnerTagHelper { Name = "Jane Smith" }, "fb-owner");
        Assert.Contains("class=\"fb-av\" aria-hidden=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains(">JS<", html, StringComparison.Ordinal);
        Assert.Contains("Jane Smith", html, StringComparison.Ordinal);
    }

    [Fact]
    public void StampNamesSourceAndAgeOrMarksManual()
    {
        var auto = Render(new StampTagHelper { Source = "AWS Config", Age = "2h ago" }, "fb-stamp");
        Assert.Contains("class=\"fb-stamp gen\"", auto, StringComparison.Ordinal);
        Assert.Contains("AWS Config", auto, StringComparison.Ordinal);
        Assert.Contains("2h ago", auto, StringComparison.Ordinal);

        var manual = Render(new StampTagHelper { Manual = true, Age = "Mar 3" }, "fb-stamp");
        Assert.Contains("class=\"fb-stamp manual\"", manual, StringComparison.Ordinal);
        Assert.Contains("MANUAL", manual, StringComparison.Ordinal);
        Assert.Contains("Mar 3", manual, StringComparison.Ordinal);
    }
}
