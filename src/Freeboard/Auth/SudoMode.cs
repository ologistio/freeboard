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

        var session = await sessionStore.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session?.SudoAt is { } sudoAt && sudoAt > DateTime.UtcNow - _options.SudoModeTtl)
        {
            context.Succeed(requirement);
        }
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
