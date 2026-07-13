using System.Linq;
using System.Text.RegularExpressions;

namespace Freeboard.Web.Tests;

/// <summary>
/// Enforces the "tokens only" invariant in the <c>@layer components</c> block of the source
/// stylesheet: no literal colour value and no built-in Tailwind palette utility. Every component
/// colour must resolve through a theme token so both themes render from one palette (S2/S3/A6).
/// </summary>
public sealed class ComponentLayerGuardTests
{
    private static string ComponentsBody()
        => CssTokenSource.StripComments(CssTokenSource.Block(CssTokenSource.Read(), "@layer components"));

    // The complete CSS named-color set (CSS Color 4), so an arbitrary named colour like
    // rebeccapurple or aliceblue is rejected, not just a handpicked few. The match uses word
    // boundaries that exclude hyphenated identifiers (e.g. --color-plum), so a bare name only hits in
    // value position.
    private static readonly string[] NamedColors =
    [
        "aliceblue", "antiquewhite", "aqua", "aquamarine", "azure", "beige", "bisque", "black",
        "blanchedalmond", "blue", "blueviolet", "brown", "burlywood", "cadetblue", "chartreuse",
        "chocolate", "coral", "cornflowerblue", "cornsilk", "crimson", "cyan", "darkblue", "darkcyan",
        "darkgoldenrod", "darkgray", "darkgreen", "darkgrey", "darkkhaki", "darkmagenta",
        "darkolivegreen", "darkorange", "darkorchid", "darkred", "darksalmon", "darkseagreen",
        "darkslateblue", "darkslategray", "darkslategrey", "darkturquoise", "darkviolet", "deeppink",
        "deepskyblue", "dimgray", "dimgrey", "dodgerblue", "firebrick", "floralwhite", "forestgreen",
        "fuchsia", "gainsboro", "ghostwhite", "gold", "goldenrod", "gray", "green", "greenyellow",
        "grey", "honeydew", "hotpink", "indianred", "indigo", "ivory", "khaki", "lavender",
        "lavenderblush", "lawngreen", "lemonchiffon", "lightblue", "lightcoral", "lightcyan",
        "lightgoldenrodyellow", "lightgray", "lightgreen", "lightgrey", "lightpink", "lightsalmon",
        "lightseagreen", "lightskyblue", "lightslategray", "lightslategrey", "lightsteelblue",
        "lightyellow", "lime", "limegreen", "linen", "magenta", "maroon", "mediumaquamarine",
        "mediumblue", "mediumorchid", "mediumpurple", "mediumseagreen", "mediumslateblue",
        "mediumspringgreen", "mediumturquoise", "mediumvioletred", "midnightblue", "mintcream",
        "mistyrose", "moccasin", "navajowhite", "navy", "oldlace", "olive", "olivedrab", "orange",
        "orangered", "orchid", "palegoldenrod", "palegreen", "paleturquoise", "palevioletred",
        "papayawhip", "peachpuff", "peru", "pink", "plum", "powderblue", "purple", "rebeccapurple",
        "red", "rosybrown", "royalblue", "saddlebrown", "salmon", "sandybrown", "seagreen", "seashell",
        "sienna", "silver", "skyblue", "slateblue", "slategray", "slategrey", "snow", "springgreen",
        "steelblue", "tan", "teal", "thistle", "tomato", "turquoise", "violet", "wheat", "white",
        "whitesmoke", "yellow", "yellowgreen",
    ];

    // The only bare colour keywords a rule body may use; every other colour must resolve through a
    // token: var(--color-...), var(--shadow...), or a color-mix() of those. Listed explicitly so the
    // allowance is deliberate, not an accident of what the named-colour scan happens to omit.
    private static readonly string[] AllowedColourKeywords = ["transparent", "currentcolor"];

    [Fact]
    public void ComponentsLayerHasNoLiteralColour()
    {
        var body = ComponentsBody();
        var hits = new List<string>();

        foreach (Match m in Regex.Matches(body, @"#[0-9a-fA-F]{3,8}\b"))
            hits.Add("hex " + m.Value);
        foreach (Match m in Regex.Matches(body, @"\b(rgb|rgba|hsl|hsla|hwb|lab|lch|oklab|oklch)\s*\("))
            hits.Add("func " + m.Value);
        foreach (var nc in NamedColors.Where(c => !AllowedColourKeywords.Contains(c, StringComparer.OrdinalIgnoreCase)))
        {
            foreach (Match m in Regex.Matches(body, $@"(^|[^-\w])({nc})(?![-\w])", RegexOptions.IgnoreCase))
                hits.Add("named " + m.Groups[2].Value);
        }

        Assert.True(hits.Count == 0,
            "Literal colour in @layer components (allowed bare keywords: "
            + string.Join(", ", AllowedColourKeywords)
            + "; every other colour must be a token: var(--color-...), var(--shadow...), color-mix()):\n"
            + string.Join("\n", hits.Distinct()));
    }

    [Fact]
    public void ComponentsLayerHasNoBuiltInPaletteUtility()
    {
        var body = ComponentsBody();
        var util = new Regex(
            @"\b(bg|text|border|ring|divide|outline|fill|stroke|from|via|to|accent|caret|placeholder|decoration|ring-offset)-"
            + @"(white|black|neutral-\d{1,3}|gray-\d{1,3}|slate-\d{1,3}|zinc-\d{1,3}|stone-\d{1,3}|red-\d{1,3}"
            + @"|green-\d{1,3}|emerald-\d{1,3}|amber-\d{1,3}|yellow-\d{1,3}|orange-\d{1,3}|rose-\d{1,3}|blue-\d{1,3}"
            + @"|sky-\d{1,3}|cyan-\d{1,3}|indigo-\d{1,3}|violet-\d{1,3}|purple-\d{1,3}|pink-\d{1,3}|fuchsia-\d{1,3}"
            + @"|teal-\d{1,3}|lime-\d{1,3})\b");

        var hits = util.Matches(body).Select(m => m.Value).Distinct().ToList();
        Assert.True(hits.Count == 0,
            "Built-in palette utility in @layer components (use a tokenized utility like bg-panel/text-ink):\n"
            + string.Join("\n", hits));
    }

    [Fact]
    public void InteractiveControlsUseAThreeToOneBoundaryToken()
    {
        // The ContrastGuard proves the boundary tokens clear 3:1; this proves the interactive controls
        // actually use them for their resting border (A1), so a control cannot regress to a low-
        // contrast divider (line/line-strong) or an alpha of a token (for example fail/30). Approved
        // boundary tokens are the ones the ContrastGuard holds at >=3:1.
        var body = ComponentsBody();
        string[] approved = ["control-outline", "fail", "brand", "ink"];
        var fails = new List<string>();

        foreach (var cls in new[] { "btn-secondary", "btn-danger", "form-input", "fb-search", "fb-chip" })
        {
            // The base rule only (a pseudo/compound selector like .fb-chip:hover has a non-space
            // char before the brace, so it is not matched).
            var rule = Regex.Match(body, $@"\.{Regex.Escape(cls)}(?![\w-])\s*\{{([^}}]*)\}}");
            if (!rule.Success) { fails.Add($"{cls}: rule not found"); continue; }
            var b = rule.Groups[1].Value;

            var tokens = new List<string>();
            foreach (Match u in Regex.Matches(b, @"\bborder-(control-outline|fail|brand|ink|line-strong|line)(/\d+)?\b"))
                tokens.Add(u.Groups[1].Value + u.Groups[2].Value);
            foreach (Match raw in Regex.Matches(b, @"border(?:-color)?\s*:[^;]*var\(--color-([a-z-]+)\)"))
                tokens.Add(raw.Groups[1].Value);

            if (tokens.Count == 0) { fails.Add($"{cls}: no border colour token"); continue; }
            foreach (var tk in tokens)
                if (tk.Contains('/') || !approved.Contains(tk))
                    fails.Add($"{cls}: resting border uses '{tk}' (need one of {string.Join("/", approved)}, no alpha)");
        }

        Assert.True(fails.Count == 0,
            "Interactive control resting boundaries must use a >=3:1 token:\n" + string.Join("\n", fails));
    }

    [Fact]
    public void ComponentsLayerHasNoBuiltInShadowUtility()
    {
        var body = ComponentsBody();
        // Tailwind's shadow utilities compile a fixed shadow colour and never resolve --shadow*, so a
        // component elevation must set box-shadow: var(--shadow...) instead. The size suffix is
        // optional so the bare `shadow`/`drop-shadow` utility (Tailwind's default elevation) is caught
        // too. The lookbehind excludes the CSS property (box-shadow) and the token (var(--shadow-md)),
        // which both carry a leading -.
        var util = new Regex(@"(?<![-\w])(shadow|drop-shadow)(-(sm|md|lg|xl|2xl|inner|none))?\b");

        var hits = util.Matches(body).Select(m => m.Value).Distinct().ToList();
        Assert.True(hits.Count == 0,
            "Built-in shadow utility in @layer components (use box-shadow: var(--shadow), var(--shadow-md), ...):\n"
            + string.Join("\n", hits));
    }
}
