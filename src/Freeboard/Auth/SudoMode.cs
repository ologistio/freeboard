using System.Security.Claims;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Freeboard.Auth;

/// <summary>
/// The sudo-mode (step-up) authorization requirement. An endpoint opts in by
/// requiring the <see cref="PolicyName"/> policy; the pipeline enforces it after bearer auth.
/// The matching POST {prefix}/auth/sudo endpoint stamps sudo_at after re-verifying a factor.
/// </summary>
public sealed class RequireSudoModeRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "RequireSudoMode";
}

/// <summary>
/// Checks that the caller's session has a recent step-up: <c>sudo_at IS NOT NULL AND
/// sudo_at > now - TTL</c> (TTL config-driven, default 5 minutes). A satisfied requirement
/// lets the endpoint run; an unsatisfied one fails the policy, which the pipeline turns into a
/// 403 for the authenticated caller.
/// </summary>
public sealed class RequireSudoModeHandler(
    ISessionStore sessionStore,
    IOptions<WebAuthOptions> options)
    : AuthorizationHandler<RequireSudoModeRequirement>
{
    private readonly WebAuthOptions _options = options.Value;

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, RequireSudoModeRequirement requirement)
    {
        var sessionId = context.User.FindFirst(AuthClaims.SessionId)?.Value;
        if (string.IsNullOrEmpty(sessionId))
        {
            return; // not authenticated as a session: leave unsatisfied (-> 401/403 upstream).
        }

        if (await SudoRecency.IsRecentAsync(sessionStore, sessionId, _options.SudoModeTtl).ConfigureAwait(false))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// The single sudo-recency predicate shared by the API's <see cref="RequireSudoModeHandler"/> and the
/// page handlers. Pipeline authorization policies do not run for in-process page handlers, so a
/// sudo-gated page must check recency itself with the exact same rule the API enforces.
/// </summary>
public static class SudoRecency
{
    /// <summary>True when the session has a step-up within <paramref name="ttl"/> of now.</summary>
    public static async Task<bool> IsRecentAsync(ISessionStore sessionStore, string? sessionId, TimeSpan ttl)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        var session = await sessionStore.GetByIdAsync(sessionId).ConfigureAwait(false);
        return session?.SudoAt is { } sudoAt && sudoAt > DateTime.UtcNow - ttl;
    }
}

/// <summary>Convenience for requiring the sudo-mode policy on an endpoint.</summary>
public static class SudoModeExtensions
{
    public static TBuilder RequireSudoMode<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization(RequireSudoModeRequirement.PolicyName);
        return builder;
    }
}
