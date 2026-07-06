using Freeboard.Core.Authz;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Freeboard.Authz;

/// <summary>
/// Selects the <see cref="AuthzResource"/> a route acts on from the request (route values and bound
/// arguments via the filter context). Returning <c>null</c> means the org-scoped resource is invisible
/// to the caller (a 404, existence non-disclosure); a throw is treated as a deny (fail closed).
/// </summary>
public delegate ValueTask<AuthzResource?> AuthzResourceSelector(EndpointFilterInvocationContext context);

/// <summary>
/// Endpoint metadata recording that a route is authz-gated. The route-metadata architecture test
/// asserts every filter-gated mutating route carries this with <see cref="AlwaysEnforce"/> true, so a
/// mutating route wired <c>alwaysEnforce: false</c> (silently mode-relaxed) is caught.
/// </summary>
public sealed record AuthzPermissionMetadata(string Action, bool AlwaysEnforce);

/// <summary>
/// The endpoint filter and its <c>RequirePermission</c> extension. The filter runs the selector,
/// calls <see cref="IAuthorizer"/>, and short-circuits a denied call: 403 (problem) for a visible
/// resource, 404 for an invisible org-scoped resource, 403 for a missing/throwing selector. Every
/// mutating route sets <c>alwaysEnforce: true</c> so its deny blocks in every mode.
/// </summary>
public static class AuthzEndpointExtensions
{
    public static RouteHandlerBuilder RequirePermission(
        this RouteHandlerBuilder builder,
        string action,
        AuthzResourceSelector resourceSelector,
        bool alwaysEnforce = false)
    {
        builder.AddEndpointFilter(new AuthzEndpointFilter(action, resourceSelector, alwaysEnforce));
        builder.WithMetadata(new AuthzPermissionMetadata(action, alwaysEnforce));
        return builder;
    }

    /// <summary>Applies the permission gate and metadata to every route in a group.</summary>
    public static RouteGroupBuilder RequirePermission(
        this RouteGroupBuilder builder,
        string action,
        AuthzResourceSelector resourceSelector,
        bool alwaysEnforce = false)
    {
        builder.AddEndpointFilter(new AuthzEndpointFilter(action, resourceSelector, alwaysEnforce));
        builder.WithMetadata(new AuthzPermissionMetadata(action, alwaysEnforce));
        return builder;
    }

    /// <summary>
    /// Declares that a route's cross-user branch requires <c>user.manage</c> (force-enforced). Used by
    /// the dual-purpose session routes, which gate the cross-user branch in-handler (no whole-route
    /// filter) but still carry the metadata so the route-metadata test records the annotation.
    /// </summary>
    public static RouteHandlerBuilder MarkCrossUserManage(this RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new AuthzPermissionMetadata(AuthzActions.UserManage, true));
        return builder;
    }

    private sealed class AuthzEndpointFilter(
        string action, AuthzResourceSelector selector, bool alwaysEnforce) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            AuthzResource? resource;
            try
            {
                resource = await selector(context).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missing/throwing selector is a deny (fail closed), never a leak.
                return Forbidden();
            }

            if (resource is null)
            {
                // The caller cannot see the org-scoped resource at all: 404, not 403.
                return Results.NotFound();
            }

            var authorizer = http.RequestServices.GetRequiredService<IAuthorizer>();
            var decision = await authorizer
                .AuthorizeAsync(http.User, action, resource, alwaysEnforce, http.RequestAborted).ConfigureAwait(false);
            if (!decision.IsPermitted)
            {
                return Forbidden();
            }

            return await next(context).ConfigureAwait(false);
        }

        private static ProblemHttpResult Forbidden() => TypedResults.Problem(
            title: "Forbidden",
            detail: "You do not have permission to perform this action.",
            statusCode: StatusCodes.Status403Forbidden,
            type: "https://freeboard.dev/problems/forbidden");
    }
}
