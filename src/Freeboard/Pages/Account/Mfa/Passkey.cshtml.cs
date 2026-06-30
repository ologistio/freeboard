using System.Text.Json;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Account.Mfa;

/// <summary>
/// Passkey registration. Under <c>/account</c>, so the named page policy requires authentication;
/// the action is additionally sudo-gated. Pipeline sudo policies do not run for in-process page
/// handlers, so this page checks sudo recency itself with the shared predicate the
/// <c>RequireSudoMode</c> policy uses and redirects to <c>/account/sudo</c> on failure (that step-up
/// page is added in a later group; until then the redirect target 404s, but the gate is enforced).
///
/// The GET fetches registration options and renders them plus an antiforgery token for the JS shim,
/// which runs <c>navigator.credentials.create</c> and POSTs the attestation JSON back with the
/// antiforgery token in the <c>RequestVerificationToken</c> header. The POST replies with a JSON
/// <c>{ redirect }</c> the shim follows; when this is the first strong factor the backend returns
/// recovery codes, which are stashed server-side and shown once on the recovery-codes display page
/// (the shim would otherwise discard the response body on a plain reload, losing the codes).
/// </summary>
public sealed class PasskeyModel(
    WebAuthnCeremony webAuthn,
    WebAuthnEnrollmentStore enrollment,
    IWebAuthnCredentialStore creds,
    ITotpStore totp,
    IRecoveryCodeStore recovery,
    IUserStore users,
    ISessionStore sessions,
    IAntiforgery antiforgery,
    RecoveryCodeDisplayStore recoveryDisplay,
    IOptions<WebAuthOptions> webAuthOptions) : PageModel
{
    private readonly WebAuthOptions _webAuth = webAuthOptions.Value;

    /// <summary>The registration options JSON for the JS ceremony.</summary>
    public string? OptionsJson { get; private set; }

    /// <summary>The server-side correlation the JS echoes back so the options can be re-found on POST.</summary>
    public string? Correlation { get; private set; }

    /// <summary>The antiforgery request token the JS shim sends in the RequestVerificationToken header.</summary>
    public string? AntiforgeryToken { get; private set; }

    /// <summary>True when WebAuthn has no configured RP id/origins, so registration cannot start.</summary>
    public bool WebAuthnUnconfigured { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireSudoAsync().ConfigureAwait(false) is { } redirect)
        {
            return redirect;
        }

        await LoadRegisterOptionsAsync(ct).ConfigureAwait(false);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (await RequireSudoAsync().ConfigureAwait(false) is { } redirect)
        {
            return redirect;
        }

        var (correlation, attestation, nickname) = await ReadAttestationAsync(ct).ConfigureAwait(false);

        var result = await AuthFlows.PasskeyRegisterAsync(
            UserId, correlation, attestation, nickname, webAuthn, enrollment, creds, totp, recovery, users,
            webAuthOptions, ct).ConfigureAwait(false);

        switch (result)
        {
            case AuthFlows.FactorActivationResult.Activated activated:
                // The shim POSTs via fetch and follows a JSON { redirect } instruction. A first-factor
                // activation returns one-time recovery codes; stash them server-side and point the shim
                // at the display page (the codes never ride the URL/cookie/client storage). A reload
                // would discard a response body, so the codes must not be in this body.
                var target = activated.RecoveryCodes is { Count: > 0 } codes
                    ? recoveryDisplay.StashAndRedirectTarget(Response, codes)
                    : "/account/mfa";
                return new JsonResult(new { redirect = target });
            case AuthFlows.FactorActivationResult.WebAuthnUnconfigured:
                WebAuthnUnconfigured = true;
                return Page();
            case AuthFlows.FactorActivationResult.Invalid invalid:
                ModelState.AddModelError(string.Empty, invalid.Message);
                // The shim swaps in the returned body, so it must carry fresh register options,
                // correlation, and antiforgery token to retry inline (a stale challenge cannot be reused).
                await LoadRegisterOptionsAsync(ct).ConfigureAwait(false);
                return Page();
            default:
                return new JsonResult(new { redirect = "/account/sudo?returnUrl=/account/mfa/passkey" });
        }
    }

    /// <summary>
    /// Fetches fresh registration options + correlation and an antiforgery token for the JS shim.
    /// Called on the GET and on the POST error re-render so the swapped-in body can retry inline.
    /// </summary>
    private async Task LoadRegisterOptionsAsync(CancellationToken ct)
    {
        var result = await AuthFlows.PasskeyRegisterOptionsAsync(UserId, webAuthn, enrollment, users, ct)
            .ConfigureAwait(false);
        switch (result)
        {
            case AuthFlows.PasskeyRegisterOptionsResult.WebAuthnUnconfigured:
                WebAuthnUnconfigured = true;
                break;
            case AuthFlows.PasskeyRegisterOptionsResult.Ok ok:
                OptionsJson = ok.OptionsJson;
                Correlation = ok.Correlation;
                AntiforgeryToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                break;
        }
    }

    private string? UserId => User.FindFirst(AuthClaims.UserId)?.Value;

    /// <summary>Redirects to the step-up page when the session lacks a recent sudo, else null to proceed.</summary>
    private async Task<IActionResult?> RequireSudoAsync()
    {
        var sessionId = User.FindFirst(AuthClaims.SessionId)?.Value;
        return await SudoRecency.IsRecentAsync(sessions, sessionId, _webAuth.SudoModeTtl).ConfigureAwait(false)
            ? null
            : Redirect("/account/sudo?returnUrl=/account/mfa/passkey");
    }

    private async Task<(string? Correlation, string? Attestation, string? Nickname)> ReadAttestationAsync(
        CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            return (
                root.TryGetProperty("correlation", out var c) ? c.GetString() : null,
                root.TryGetProperty("attestation", out var a) ? a.GetString() : null,
                root.TryGetProperty("nickname", out var n) ? n.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }
}
