namespace Freeboard.Core.Authz;

/// <summary>
/// The pure decision core: evaluates a request against the policy pipeline and returns an effect.
/// I/O-free, so policy logic is fully unit-testable without a database or ASP.NET.
/// </summary>
public interface IAuthorizationEngine
{
    AuthzDecision Evaluate(AuthzRequest request);
}

/// <summary>
/// Runs an ordered set of policies and combines them deny-overrides, default-deny: any policy
/// <see cref="AuthzPolicyOutcome.Deny"/> wins; otherwise the first <see cref="AuthzPolicyOutcome.Permit"/>
/// wins; otherwise the request is denied (fail closed). Deny is checked across ALL policies before
/// any permit takes effect, so a hard-deny contributor (e.g. the session guard) can never be
/// overridden by a later permit.
/// </summary>
public sealed class PolicyAuthorizationEngine(IEnumerable<IAuthzPolicy> policies) : IAuthorizationEngine
{
    private readonly IReadOnlyList<IAuthzPolicy> _policies = policies.ToList();

    public AuthzDecision Evaluate(AuthzRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? permitReason = null;
        foreach (var policy in _policies)
        {
            var outcome = policy.Evaluate(request);
            if (outcome == AuthzPolicyOutcome.Deny)
            {
                return AuthzDecision.Deny($"denied by {policy.Name}");
            }

            if (outcome == AuthzPolicyOutcome.Permit && permitReason is null)
            {
                permitReason = $"permitted by {policy.Name}";
            }
        }

        return permitReason is not null
            ? AuthzDecision.Permit(permitReason)
            : AuthzDecision.Deny("default deny: no policy permitted");
    }
}
