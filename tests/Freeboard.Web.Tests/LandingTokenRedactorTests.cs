using Freeboard.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;

namespace Freeboard.Web.Tests;

/// <summary>
/// The request-log interceptor that keeps the single-use token off the emailed-link landing paths
/// out of this app's request log. It must scrub only the two landing GETs, and only when a token is
/// present, replacing the query with a REDACTED marker rather than logging the secret.
/// </summary>
public sealed class LandingTokenRedactorTests
{
    private static HttpLoggingInterceptorContext ContextFor(string path, string? query)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        if (query is not null)
        {
            httpContext.Request.QueryString = new QueryString(query);
        }

        return new HttpLoggingInterceptorContext { HttpContext = httpContext };
    }

    private static string? RedactedPathAndQuery(HttpLoggingInterceptorContext context)
        => context.Parameters
            .Where(p => p.Key == "PathAndQuery")
            .Select(p => p.Value as string)
            .FirstOrDefault();

    [Fact]
    public async Task NonLandingPathIsLeftUntouched()
    {
        var context = ContextFor("/dashboard", "?token=secret");
        // A sentinel the interceptor must not change when it bails out on a non-landing path.
        context.LoggingFields = HttpLoggingFields.All;

        await new LandingTokenRedactor().OnRequestAsync(context);

        Assert.Equal(HttpLoggingFields.All, context.LoggingFields);
        Assert.Null(RedactedPathAndQuery(context));
    }

    [Fact]
    public async Task LandingPathWithoutTokenLogsBarePath()
    {
        var context = ContextFor("/reset-password", query: null);

        await new LandingTokenRedactor().OnRequestAsync(context);

        Assert.Equal(HttpLoggingFields.RequestPath, context.LoggingFields);
        Assert.Null(RedactedPathAndQuery(context));
    }

    [Theory]
    [InlineData("/reset-password")]
    [InlineData("/auth/magic-link")]
    public async Task LandingPathWithTokenIsRedacted(string path)
    {
        var context = ContextFor(path, "?token=secret");

        await new LandingTokenRedactor().OnRequestAsync(context);

        Assert.Equal(HttpLoggingFields.None, context.LoggingFields);
        Assert.Equal($"{path}?token=REDACTED", RedactedPathAndQuery(context));
    }
}
