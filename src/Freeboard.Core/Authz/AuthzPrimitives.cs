namespace Freeboard.Core.Authz;

/// <summary>The outcome of an authorization decision. Deny-by-default: absence of a permit is a deny.</summary>
public enum AuthzEffect
{
    Deny,
    Permit,
}

/// <summary>
/// A decision plus a machine reason. <see cref="Reason"/> is for audit and debugging ONLY and is
/// never surfaced to the caller (it can name internal policy detail).
/// </summary>
public sealed record AuthzDecision(AuthzEffect Effect, string Reason)
{
    public bool IsPermitted => Effect == AuthzEffect.Permit;

    public static AuthzDecision Permit(string reason) => new(AuthzEffect.Permit, reason);

    public static AuthzDecision Deny(string reason) => new(AuthzEffect.Deny, reason);
}
