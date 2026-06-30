using System.Text.Encodings.Web;
using Freeboard.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Freeboard.Web;

/// <summary>
/// The page-scoped authentication scheme used only by the protected-page authorization policy.
///
/// Credential validation is NOT reimplemented here: the scheme forwards authenticate to
/// <see cref="AuthClaims.Scheme"/> (the unchanged bearer handler), which the cookie bridge feeds via
/// the injected <c>Authorization</c> header. This scheme exists only to convert the authorization
/// challenge/forbid outcomes into browser redirects:
/// - <see cref="HandleChallengeAsync"/> -> <c>302 /login?returnUrl=...</c> (no/invalid credentials).
/// - <see cref="HandleForbiddenAsync"/> -> <c>302 /account/sudo?returnUrl=...</c>, a generic-forbid
///   fallback. The real sudo redirect for sudo-gated page actions comes from the page handler's own
///   sudo-recency check, so this branch is effectively unused.
///
/// It is wired ONLY to the <c>/account</c> folder via a named policy, never as the process-wide
/// default/fallback, so the JSON API keeps emitting the bearer handler's bare 401/403.
/// </summary>
public sealed class PageChallengeScheme(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The scheme name the named page policy selects.</summary>
    public const string SchemeName = "PageChallenge";

    /// <summary>The named authorization policy bound to the protected page folder.</summary>
    public const string PolicyName = "PageAuthenticated";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Context.AuthenticateAsync(AuthClaims.Scheme);

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Redirect($"/login{ReturnUrlQuery()}");
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Redirect($"/account/sudo{ReturnUrlQuery()}");
        return Task.CompletedTask;
    }

    private string ReturnUrlQuery()
    {
        var target = Request.Path + Request.QueryString;
        return LocalRedirect.IsLocal(target)
            ? $"?returnUrl={Uri.EscapeDataString(target)}"
            : string.Empty;
    }
}
