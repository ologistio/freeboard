using System.Text.RegularExpressions;

namespace Freeboard.Web.Tests;

/// <summary>
/// Reads and parses the SOURCE stylesheet (<c>src/Freeboard/assets/css/app.css</c>), not the
/// gitignored minified build output, so the token and components-layer guards assert against the
/// single source of truth.
/// </summary>
internal static class CssTokenSource
{
    internal static string Read()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "Freeboard", "assets", "css", "app.css");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("Could not locate src/Freeboard/assets/css/app.css from " + dir);
    }

    /// <summary>Returns the body between the braces of the first <c>@block {...}</c> whose header matches.</summary>
    internal static string Block(string css, string headerContains)
    {
        var start = css.IndexOf(headerContains, StringComparison.Ordinal);
        if (start < 0) throw new ArgumentException($"Block '{headerContains}' not found");
        var open = css.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return css[(open + 1)..i];
        }
        throw new ArgumentException($"Unbalanced braces after '{headerContains}'");
    }

    /// <summary>Strips <c>/* ... */</c> comments.</summary>
    internal static string StripComments(string css) => Regex.Replace(css, @"/\*[\s\S]*?\*/", "");

    /// <summary>Parses <c>--name: value;</c> custom-property declarations into a map.</summary>
    internal static Dictionary<string, string> Tokens(string block)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(StripComments(block), @"(--[a-z0-9-]+)\s*:\s*([^;]+);"))
        {
            map[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        }
        return map;
    }
}
