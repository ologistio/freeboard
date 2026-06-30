using System.Net;
using System.Text.RegularExpressions;

namespace Freeboard.Web.Tests;

/// <summary>
/// Helpers for driving antiforgery-protected page POSTs in tests: fetch a page, scrape its
/// antiforgery form token and cookie, and resubmit them with the form fields. The Razor Pages global
/// antiforgery convention rejects any POST without a matching token + cookie pair.
/// </summary>
internal static partial class AuthFormTestHelpers
{
    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryFieldRegex();

    /// <summary>
    /// GETs <paramref name="path"/>, scrapes the antiforgery token + cookie, then POSTs the form
    /// fields back with both, optionally carrying an existing session/cookie set.
    /// </summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string path,
        IEnumerable<KeyValuePair<string, string>> fields,
        IEnumerable<KeyValuePair<string, string>>? extraCookies = null,
        string? getPath = null)
    {
        var cookies = new List<KeyValuePair<string, string>>();
        if (extraCookies is not null)
        {
            cookies.AddRange(extraCookies);
        }

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, getPath ?? path);
        ApplyCookies(getRequest, cookies);
        using var getResponse = await client.SendAsync(getRequest);
        var html = await getResponse.Content.ReadAsStringAsync();

        var token = AntiforgeryFieldRegex().Match(html).Groups[1].Value;
        cookies.AddRange(ParseSetCookies(getResponse));

        var form = new List<KeyValuePair<string, string>>(fields)
        {
            new("__RequestVerificationToken", token),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form),
        };
        ApplyCookies(request, cookies);
        return await client.SendAsync(request);
    }

    /// <summary>The cookie name=value pairs a response set (ignoring attributes and deletions).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ParseSetCookies(HttpResponseMessage response)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return result;
        }

        foreach (var nameValue in setCookies.Select(raw => raw.Split(';', 2)[0]))
        {
            var eq = nameValue.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = nameValue[..eq];
            var value = nameValue[(eq + 1)..];
            // A deletion sets an empty value with an expired date; drop those so they do not mask a real cookie.
            if (!string.IsNullOrEmpty(value))
            {
                result.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return result;
    }

    /// <summary>True when the response set a cookie of the given name to an empty (cleared) value.</summary>
    public static bool ClearsCookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return false;
        }

        foreach (var nameValue in setCookies.Select(raw => raw.Split(';', 2)[0]))
        {
            if (nameValue.StartsWith($"{name}=", StringComparison.Ordinal)
                && nameValue.Length == name.Length + 1)
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyCookies(HttpRequestMessage request, IReadOnlyList<KeyValuePair<string, string>> cookies)
    {
        if (cookies.Count == 0)
        {
            return;
        }

        // Later entries (set during the GET) override earlier ones for the same name.
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in cookies)
        {
            byName[name] = value;
        }

        request.Headers.Add("Cookie", string.Join("; ", byName.Select(kv => $"{kv.Key}={kv.Value}")));
    }
}
