using System.Globalization;
using System.Text.RegularExpressions;

namespace Freeboard.Web.Tests;

/// <summary>
/// Computes WCAG contrast ratios from the token values in the source stylesheet and holds the AA
/// line in BOTH themes (A1 and the AA-in-both-themes half of A6). Text pairs must clear 4.5:1; a
/// seal/fill against its ground must clear 3:1. Dark is checked from its authored token set: dark
/// applies only via an explicit override, but the override reaches those values.
/// </summary>
public sealed class ContrastGuardTests
{
    private sealed record Rgb(double R, double G, double B);

    private static Rgb Parse(string value, Rgb ground)
    {
        value = value.Trim();
        var hex = Regex.Match(value, @"^#([0-9a-fA-F]{6})$");
        if (hex.Success)
        {
            var h = hex.Groups[1].Value;
            return new Rgb(Conv(h, 0), Conv(h, 2), Conv(h, 4));
            static double Conv(string s, int i) => int.Parse(s.Substring(i, 2), NumberStyles.HexNumber);
        }
        var rgba = Regex.Match(value, @"rgba?\(\s*([\d.]+)[ ,]+([\d.]+)[ ,]+([\d.]+)(?:[ ,/]+([\d.]+))?\s*\)");
        if (rgba.Success)
        {
            double R = D(1), G = D(2), B = D(3), A = rgba.Groups[4].Success ? D(4) : 1.0;
            // Composite over the opaque ground.
            return new Rgb(R * A + ground.R * (1 - A), G * A + ground.G * (1 - A), B * A + ground.B * (1 - A));
            double D(int g) => double.Parse(rgba.Groups[g].Value, CultureInfo.InvariantCulture);
        }
        throw new FormatException("Unparseable colour: " + value);
    }

    private static double Lum(Rgb c)
    {
        static double Ch(double v) { v /= 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static double Ratio(Rgb a, Rgb b)
    {
        double la = Lum(a), lb = Lum(b), hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static (Dictionary<string, string> tokens, Rgb panel, Rgb field, Rgb pdim) Theme(string css, bool dark)
    {
        var block = dark
            ? CssTokenSource.Block(css, "html[data-theme=\"dark\"]")
            : CssTokenSource.Block(css, "@theme");
        var t = CssTokenSource.Tokens(block);
        var opaque = new Rgb(0, 0, 0);
        return (t, Parse(t["--color-panel"], opaque), Parse(t["--color-field"], opaque), Parse(t["--color-panel-dim"], opaque));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextPairsClearAa(bool dark)
    {
        var css = CssTokenSource.Read();
        var (t, panel, field, pdim) = Theme(css, dark);
        Rgb C(string name, Rgb ground) => Parse(t[name], ground);

        var fails = new List<string>();
        void Text(string fg, Rgb ground, string label)
        {
            var r = Ratio(C(fg, ground), ground);
            if (r < 4.5) fails.Add($"{label}: {r:F2}");
        }

        // Neutral text on the three grounds.
        foreach (var (name, _) in new[] { ("--color-ink", 0), ("--color-muted", 0), ("--color-faint", 0) })
        {
            Text(name, panel, $"{name} on panel");
            Text(name, field, $"{name} on field");
            Text(name, pdim, $"{name} on panel-dim");
        }

        // Semantic word (-ink) on its soft ground and on every panel ground a badge/tag can render
        // on: panel, field, and panel-dim.
        foreach (var s in new[] { "ok", "warn", "fail", "info", "neutral" })
        {
            var soft = C($"--color-{s}-soft", panel);
            Text($"--color-{s}-ink", soft, $"{s}-ink on {s}-soft");
            Text($"--color-{s}-ink", panel, $"{s}-ink on panel");
            Text($"--color-{s}-ink", field, $"{s}-ink on field");
            Text($"--color-{s}-ink", pdim, $"{s}-ink on panel-dim");
        }

        // Brand word on its soft ground and on the panel grounds; button label on the solid brand fill.
        Text("--color-brand-ink", C("--color-brand-soft", panel), "brand-ink on brand-soft");
        Text("--color-brand-ink", panel, "brand-ink on panel");
        Text("--color-brand-ink", field, "brand-ink on field");
        Text("--color-brand-ink", pdim, "brand-ink on panel-dim");
        Text("--color-on-brand", C("--color-brand", panel), "on-brand on brand");

        Assert.True(fails.Count == 0, $"AA text failures ({(dark ? "dark" : "light")}):\n" + string.Join("\n", fails));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SealFillsClearGraphicalContrast(bool dark)
    {
        var css = CssTokenSource.Read();
        var (t, panel, field, _) = Theme(css, dark);
        var fails = new List<string>();
        foreach (var s in new[] { "ok", "warn", "fail", "info", "neutral" })
        {
            var soft = Parse(t[$"--color-{s}-soft"], panel);
            var baseC = Parse(t[$"--color-{s}"], panel);
            var r = Ratio(baseC, soft);
            if (r < 3.0) fails.Add($"{s} seal on {s}-soft: {r:F2}");
        }

        // The off-seal (Snoozed/Waiting/Draft/OutOfScope) is a transparent square outlined in
        // --color-neutral; its border must clear 3:1 against both grounds it renders on.
        var neutral = Parse(t["--color-neutral"], panel);
        var offPanel = Ratio(neutral, panel);
        if (offPanel < 3.0) fails.Add($"off-seal neutral border on panel: {offPanel:F2}");
        var offField = Ratio(neutral, field);
        if (offField < 3.0) fails.Add($"off-seal neutral border on field: {offField:F2}");

        Assert.True(fails.Count == 0, $"Seal contrast failures ({(dark ? "dark" : "light")}):\n" + string.Join("\n", fails));
    }
}
