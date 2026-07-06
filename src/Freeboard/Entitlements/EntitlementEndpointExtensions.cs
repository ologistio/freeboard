using Freeboard.Core.Enterprise;

namespace Freeboard.Entitlements;

/// <summary>
/// The <c>RequireEntitlement</c> endpoint filter (mirrors <c>RequirePermission</c>). It resolves
/// <see cref="IEnterpriseEntitlements"/> from request services and short-circuits with 404 when the
/// install is not entitled, so an unentitled feature is absent (not forbidden). Applied AHEAD of the
/// permission gate, so even a super-admin gets 404 on an off feature. Routes stay mapped
/// unconditionally; the filter, not conditional mapping, does the gating.
/// </summary>
public static class EntitlementEndpointExtensions
{
    public static RouteGroupBuilder RequireEntitlement(this RouteGroupBuilder builder, EnterpriseEntitlement entitlement)
    {
        builder.AddEndpointFilter(new EntitlementEndpointFilter(entitlement));
        return builder;
    }

    public static RouteHandlerBuilder RequireEntitlement(this RouteHandlerBuilder builder, EnterpriseEntitlement entitlement)
    {
        builder.AddEndpointFilter(new EntitlementEndpointFilter(entitlement));
        return builder;
    }

    private sealed class EntitlementEndpointFilter(EnterpriseEntitlement entitlement) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var entitlements = context.HttpContext.RequestServices.GetRequiredService<IEnterpriseEntitlements>();
            return entitlements.IsEntitled(entitlement)
                ? await next(context).ConfigureAwait(false)
                : Results.NotFound();
        }
    }
}
