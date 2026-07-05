namespace Freeboard.Core.Enterprise;

/// <summary>
/// A named enterprise feature that an install may or may not be entitled to use. Members are numbered
/// from 1 so the default value 0 stays unmapped and resolves to not-entitled (fail-safe).
/// </summary>
public enum EnterpriseEntitlement
{
    /// <summary>Custom authorization policies.</summary>
    CustomPolicies = 1,
}
