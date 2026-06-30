using System.Text.Json;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Account;

/// <summary>
/// Sudo (step-up) interstitial. Sudo-gated <c>/account</c> actions redirect here with a local
/// <c>returnUrl</c> when their step-up is stale; this page re-confirms one of the user's currently
/// usable factors and, on success, stamps <c>sudo_at</c> (inside the shared flow) and resumes the
/// validated local target. It offers password / TOTP / passkey / recovery only - NOT magic-link,
/// which cannot work under the Strict session cookie and is out of web scope.
///
/// The password / TOTP / recovery factors are plain antiforgery forms. The passkey factor uses the JS
/// shim: the GET renders assertion options the shim runs, and the shim POSTs the assertion JSON with
/// the antiforgery token in the <c>RequestVerificationToken</c> header; that POST replies with a JSON
/// <c>{ redirect }</c> the shim follows. The returnUrl is validated as a local path on every branch.
/// </summary>
public sealed class SudoModel(
    IUserStore users,
    IPasswordCredentialStore credentials,
    IPasswordHasher hasher,
    ITotpStore totp,
    IRecoveryCodeStore recovery,
    WebAuthnCeremony webAuthn,
    WebAuthnEnrollmentStore enrollment,
    IMfaChallengeStore challenges,
    ISessionStore sessions,
    AuthRateLimiter rateLimiter,
    MfaFactorService mfaFactors,
    IAntiforgery antiforgery) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>True when the non-MFA user re-confirms a password (no strong factor enrolled).</summary>
    public bool OfferPassword { get; private set; }

    public bool OfferTotp { get; private set; }

    public bool OfferRecovery { get; private set; }

    public bool OfferPasskey { get; private set; }

    /// <summary>The assertion options JSON for the passkey ceremony, set when passkey is offered.</summary>
    public string? PasskeyOptionsJson { get; private set; }

    /// <summary>The server-side correlation the JS echoes back so the options can be re-found on POST.</summary>
    public string? PasskeyCorrelation { get; private set; }

    /// <summary>The antiforgery request token the JS shim sends in the RequestVerificationToken header.</summary>
    public string? AntiforgeryToken { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var user = await users.GetByIdAsync(UserId ?? string.Empty, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Redirect("/login");
        }

        await ComputeOfferedFactorsAsync(user, ct).ConfigureAwait(false);
        if (OfferPasskey)
        {
            var options = await AuthFlows.SudoPasskeyOptionsAsync(UserId, webAuthn, enrollment, ct).ConfigureAwait(false);
            if (options is AuthFlows.SudoPasskeyOptionsResult.Ok ok)
            {
                PasskeyOptionsJson = ok.OptionsJson;
                PasskeyCorrelation = ok.Correlation;
                AntiforgeryToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            }
            else
            {
                // WebAuthn unconfigured (or no user): hide the passkey option rather than render a broken ceremony.
                OfferPasskey = false;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        string? factor, string? password, string? code, string? recovery_code, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(UserId ?? string.Empty, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Redirect("/login");
        }

        // The passkey factor arrives as the shim's JSON fetch ({ correlation, assertion }); the other
        // factors arrive as form fields. Read the assertion payload only on the JSON path.
        string? correlation = null;
        string? assertion = null;
        if (Request.HasJsonContentType())
        {
            factor = MfaFactors.Passkey;
            (correlation, assertion) = await ReadAssertionAsync(ct).ConfigureAwait(false);
        }

        var result = await AuthFlows.SudoAsync(
            UserId, SessionId, HttpContext.Connection.RemoteIpAddress?.ToString(),
            factor, password, code, recovery_code, correlation, assertion,
            challengeId: null, linkToken: null,
            users, credentials, hasher, totp, recovery, webAuthn, enrollment, challenges, sessions, rateLimiter, ct)
            .ConfigureAwait(false);

        var target = Freeboard.Web.LocalRedirect.Sanitize(Url, ReturnUrl);
        if (result is AuthFlows.SudoResult.Ok)
        {
            return Request.HasJsonContentType()
                ? new JsonResult(new { redirect = target })
                : Redirect(target);
        }

        // A failed verify or rate-limit surfaces one generic message; re-render the offered factors.
        await ComputeOfferedFactorsAsync(user, ct).ConfigureAwait(false);
        if (Request.HasJsonContentType())
        {
            // The shim re-renders the returned HTML in place, so it must carry the offered factors.
            if (OfferPasskey)
            {
                var options = await AuthFlows.SudoPasskeyOptionsAsync(UserId, webAuthn, enrollment, ct).ConfigureAwait(false);
                if (options is AuthFlows.SudoPasskeyOptionsResult.Ok ok)
                {
                    PasskeyOptionsJson = ok.OptionsJson;
                    PasskeyCorrelation = ok.Correlation;
                    AntiforgeryToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                }
            }
        }

        ModelState.AddModelError(string.Empty, "That did not confirm your identity. Try again.");
        return Page();
    }

    private async Task ComputeOfferedFactorsAsync(UserRow user, CancellationToken ct)
    {
        // A non-MFA user re-confirms their password; an MFA user uses one of their strong factors. Drop
        // magic-link: it is out of web scope (the Strict session cookie is not sent on the emailed GET).
        OfferPassword = !user.MfaEnabled;
        var available = await mfaFactors.AvailableAsync(user, ct).ConfigureAwait(false);
        OfferTotp = available.Contains(MfaFactors.Totp);
        OfferRecovery = available.Contains(MfaFactors.Recovery);
        OfferPasskey = available.Contains(MfaFactors.Passkey);
    }

    private string? UserId => User.FindFirst(AuthClaims.UserId)?.Value;

    private string? SessionId => User.FindFirst(AuthClaims.SessionId)?.Value;

    private async Task<(string? Correlation, string? Assertion)> ReadAssertionAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            return (
                root.TryGetProperty("correlation", out var c) ? c.GetString() : null,
                root.TryGetProperty("assertion", out var a) ? a.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
