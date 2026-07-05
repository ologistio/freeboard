namespace Freeboard.Core.Authz;

/// <summary>A single policy's verdict on a request. <see cref="NotApplicable"/> abstains.</summary>
public enum AuthzPolicyOutcome
{
    NotApplicable,
    Permit,
    Deny,
}

/// <summary>
/// One contributor in the ordered deny-overrides pipeline. Each policy inspects the request and
/// returns permit, deny, or not-applicable; the engine combines them deny-overrides, default-deny.
/// </summary>
public interface IAuthzPolicy
{
    /// <summary>A stable name used in the decision reason for audit.</summary>
    string Name { get; }

    AuthzPolicyOutcome Evaluate(AuthzRequest request);
}
