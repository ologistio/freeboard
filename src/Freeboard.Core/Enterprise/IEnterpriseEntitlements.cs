namespace Freeboard.Core.Enterprise;

/// <summary>
/// The MIT gate that decides whether an install is entitled to a named enterprise feature. Kept in
/// Core so any MIT caller (including the pure authz engine) can ask without referencing Freeboard.Enterprise.
/// </summary>
public interface IEnterpriseEntitlements
{
    bool IsEntitled(EnterpriseEntitlement entitlement);
}
