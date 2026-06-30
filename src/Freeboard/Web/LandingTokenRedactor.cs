using Microsoft.AspNetCore.HttpLogging;

namespace Freeboard.Web;

/// <summary>
/// Redacts the single-use <c>token</c> query value from HTTP request logs on the emailed-link
/// landing paths (<c>/reset-password</c> and <c>/auth/magic-link</c>). The inbound GET necessarily
/// arrives with <c>?token=&lt;secret&gt;</c> in the URL; this keeps that secret out of this app's
/// request log. The real request URL is untouched, so the scrub handler still reads the live token.
///
/// HTTP logging is otherwise off (no fields), so this adds no request-log noise to other routes; it
/// only emits a redacted path line for the two landing GETs.
/// </summary>
public sealed class LandingTokenRedactor : IHttpLoggingInterceptor
{
    public ValueTask OnRequestAsync(HttpLoggingInterceptorContext logContext)
    {
        var path = logContext.HttpContext.Request.Path;
        var isLandingPath = path.Equals("/reset-password", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/auth/magic-link", StringComparison.OrdinalIgnoreCase);
        if (!isLandingPath)
        {
            return ValueTask.CompletedTask;
        }

        logContext.LoggingFields = HttpLoggingFields.RequestPath;
        if (logContext.HttpContext.Request.Query.ContainsKey("token"))
        {
            // Log the bare path with the token query replaced; never the secret.
            logContext.AddParameter("PathAndQuery", $"{path}?token=REDACTED");
            logContext.LoggingFields = HttpLoggingFields.None;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnResponseAsync(HttpLoggingInterceptorContext logContext) => ValueTask.CompletedTask;
}
