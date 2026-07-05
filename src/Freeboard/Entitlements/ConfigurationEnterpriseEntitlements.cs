using Freeboard.Core.Enterprise;

namespace Freeboard.Entitlements;

/// <summary>
/// Reads entitlement flags from <see cref="IConfiguration"/>. This is MIT DI plumbing, not a paid
/// feature: a future license-key provider replaces the single DI registration in Program.cs and
/// callers stay on the interface. An unmapped or absent entitlement resolves to not-entitled, so a
/// paid feature is never leaked by a missing config key.
/// </summary>
public sealed class ConfigurationEnterpriseEntitlements(IConfiguration configuration) : IEnterpriseEntitlements
{
    public bool IsEntitled(EnterpriseEntitlement entitlement) => entitlement switch
    {
        EnterpriseEntitlement.CustomPolicies => configuration.GetValue<bool>("Enterprise:CustomPolicies"),
        _ => false,
    };
}
