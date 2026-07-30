using Markdig;
using Markdig.Extensions.CustomContainers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Freeboard.Web.Docs;

/// <summary>
/// Renders a <c>tabs</c> custom container as a tab strip. Markdown authors write:
///
/// <code>
/// ::::tabs model
/// :::tab UI
/// body
/// :::
/// ::::
/// </code>
///
/// The container argument (<c>model</c> above) names the tab group. Every group with the
/// same name on a page shares one selection, so a reader who picks GitOps once reads the
/// whole page in GitOps.
/// </summary>
public sealed class DocsTabsExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer html)
        {
            html.ObjectRenderers.Replace<HtmlCustomContainerRenderer>(new DocsTabsRenderer());
        }
    }
}

internal sealed class DocsTabsRenderer : HtmlObjectRenderer<CustomContainer>
{
    private const string TabsInfo = "tabs";
    private const string TabInfo = "tab";
    private const string DefaultGroup = "docs";

    protected override void Write(HtmlRenderer renderer, CustomContainer container)
    {
        var tabs = container.OfType<CustomContainer>()
            .Where(c => string.Equals(c.Info, TabInfo, StringComparison.Ordinal))
            .ToList();

        if (!string.Equals(container.Info, TabsInfo, StringComparison.Ordinal) || tabs.Count == 0)
        {
            // Any other container falls back to a plain div, so a mistyped fence shows its
            // content rather than dropping it.
            renderer.Write("<div").WriteAttributes(container).WriteLine(">");
            renderer.WriteChildren(container);
            renderer.WriteLine("</div>");
            return;
        }

        // The container argument labels the tab strip for a screen reader and, slugified, names
        // the group the strip belongs to.
        var label = string.IsNullOrWhiteSpace(container.Arguments) ? "Tabs" : container.Arguments.Trim();
        var group = Slug(container.Arguments) is { Length: > 0 } named ? named : DefaultGroup;
        var keys = Keys(tabs);
        // The source line makes the ids unique within the page without a render-time counter,
        // which a pipeline-lifetime renderer instance cannot hold safely.
        var prefix = $"fbt-{container.Line}";

        renderer.Write("<div class=\"fb-tabs\" x-data=\"fbTabs('").Write(group).Write("', [");
        renderer.Write(string.Join(", ", keys.Select(k => $"'{k}'"))).WriteLine("])\">");
        renderer.Write("<div class=\"fb-tabs__list\" role=\"tablist\" aria-label=\"");
        renderer.WriteEscape(label).WriteLine("\">");

        for (var i = 0; i < tabs.Count; i++)
        {
            renderer.Write("<button type=\"button\" role=\"tab\" class=\"fb-tabs__tab\"")
                .Write($" id=\"{prefix}-tab-{i}\" aria-controls=\"{prefix}-panel-{i}\"")
                .Write($" :aria-selected=\"on('{keys[i]}')\" :tabindex=\"on('{keys[i]}') ? 0 : -1\"")
                .Write($" :class=\"{{ 'is-on': on('{keys[i]}') }}\" @click=\"pick('{keys[i]}')\"")
                .Write(" @keydown=\"step($event)\">");
            renderer.WriteEscape(Label(tabs[i])).WriteLine("</button>");
        }

        renderer.WriteLine("</div>");

        for (var i = 0; i < tabs.Count; i++)
        {
            // Hidden inline rather than by class: x-show clears the inline display to reveal a
            // panel, so a CSS rule (or the hidden attribute) would keep winning and stay hidden.
            var hidden = i == 0 ? string.Empty : " style=\"display:none\"";
            renderer.Write("<div class=\"fb-tabs__panel\" role=\"tabpanel\"")
                .Write($" id=\"{prefix}-panel-{i}\" aria-labelledby=\"{prefix}-tab-{i}\"")
                .Write($" x-show=\"on('{keys[i]}')\"").Write(hidden).WriteLine(">");
            renderer.WriteChildren(tabs[i]);
            renderer.WriteLine("</div>");
        }

        renderer.WriteLine("</div>");
    }

    private static string Label(CustomContainer tab) =>
        string.IsNullOrWhiteSpace(tab.Arguments) ? TabInfo : tab.Arguments.Trim();

    /// <summary>
    /// One selection key per tab, taken from its label so groups sharing a name track each
    /// other by label rather than by position. A repeated label is suffixed, since two tabs
    /// answering to one key would open together.
    /// </summary>
    private static List<string> Keys(List<CustomContainer> tabs)
    {
        var keys = new List<string>(tabs.Count);
        foreach (var tab in tabs)
        {
            var key = Slug(Label(tab));
            key = key.Length == 0 ? TabInfo : key;
            var unique = key;
            for (var n = 2; keys.Contains(unique, StringComparer.Ordinal); n++)
            {
                unique = $"{key}-{n}";
            }

            keys.Add(unique);
        }

        return keys;
    }

    /// <summary>
    /// Reduces authored text to lowercase letters, digits and hyphens. Keys and group names go
    /// into HTML attributes as JavaScript string literals, so this is also what keeps them from
    /// needing to be escaped there.
    /// </summary>
    private static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var chars = text.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }
}
