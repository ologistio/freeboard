namespace Freeboard.Api;

/// <summary>
/// The single API namespace. All web API routes live under one prefix; the auth group
/// and the read endpoints share it. One constant keeps the prefix in one place.
/// </summary>
public static class ApiRoutes
{
    /// <summary>The one API prefix all routes hang off.</summary>
    public const string ApiRoutePrefix = "/api/v1/freeboard";
}

/// <summary>
/// Endpoint metadata marker for auth endpoints. The GitOps read-only middleware reads
/// this off the matched endpoint and skips its 409 ONLY for marked endpoints, so auth (all
/// POST) works in read-only mode while other mutating routes - including non-auth routes under
/// the API prefix - still get 409. Tag an endpoint by calling
/// <c>.WithMetadata(new AuthEndpoint())</c> (or the <see cref="AuthEndpointExtensions"/> helper).
/// </summary>
public sealed class AuthEndpoint;

/// <summary>Convenience for tagging an endpoint with the <see cref="AuthEndpoint"/> marker.</summary>
public static class AuthEndpointExtensions
{
    public static TBuilder MarkAuthEndpoint<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new AuthEndpoint());
        return builder;
    }
}

/// <summary>
/// Endpoint metadata marker for the page routes a force-reset (limited) session is allowed to
/// reach: the forced-reset completion page, logout, and the account landing. The force-reset guard
/// reads this off the matched endpoint and permits the request in addition to its exact-path API
/// allowlist, so a limited browser session can complete the reset funnel instead of being 403'd.
/// Independent of <see cref="AuthEndpoint"/> (read by a different middleware); a route can carry both.
/// </summary>
public sealed class LimitedSessionAllowed;
