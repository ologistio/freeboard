namespace Freeboard.Web;

/// <summary>
/// The organisation-selection endpoint. A GET so it works in GitOps read-only mode (the read-only
/// middleware blocks only unsafe methods) and the selector links need no per-node antiforgery token.
/// It sets or clears the <c>freeboard-org</c> view-preference cookie and redirects back to the
/// selecting page. Behind the named page-challenge policy so an anonymous browser is 302-redirected
/// to <c>/login</c> (the process-wide bearer default would 401 instead).
/// </summary>
public static class OrgSelectEndpoints
{
    /// <summary>Where a non-local return target falls back to: an app page, not the /account default.</summary>
    private const string Fallback = "/compliance/statement-of-applicability";

    public static void MapOrgSelectEndpoints(this WebApplication app)
    {
        app.MapGet("/org/select", (HttpContext http, string? org, string? @return) =>
            {
                if (string.IsNullOrEmpty(org))
                {
                    OrgSelection.Clear(http.Response);
                }
                else
                {
                    OrgSelection.Set(http.Response, org);
                }

                return Results.Redirect(LocalRedirect.Sanitize(@return, Fallback));
            })
            .RequireAuthorization(PageChallengeScheme.PolicyName);
    }
}
