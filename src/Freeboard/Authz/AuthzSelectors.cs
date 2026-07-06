using Freeboard.Core.Authz;

namespace Freeboard.Authz;

/// <summary>
/// Shared <see cref="AuthzResourceSelector"/> instances. <see cref="System"/> is security-relevant (the
/// break-glass <c>system</c> resource behind <c>system.admin</c>) and identical across the system-scoped
/// routes, so it has one definition here rather than a copy per endpoint class.
/// </summary>
internal static class AuthzSelectors
{
    public static ValueTask<AuthzResource?> System(EndpointFilterInvocationContext context)
        => ValueTask.FromResult<AuthzResource?>(new AuthzResource("system", null, null, []));
}
