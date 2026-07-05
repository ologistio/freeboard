namespace Freeboard.Core.Authz;

/// <summary>
/// The principal's effective authorization facts: the system permission keys it holds and its
/// effective org grants. A pure port (no persistence dependency) so the engine and its inputs stay
/// I/O-free and unit-testable; the web layer implements it over the persistence store.
/// </summary>
public sealed record AuthzPrincipalFacts(
    IReadOnlySet<string> SystemPermissions,
    IReadOnlyCollection<AuthzOrgGrant> OrgGrants)
{
    public static readonly AuthzPrincipalFacts None = new(
        new HashSet<string>(StringComparer.Ordinal), []);
}

/// <summary>
/// Loads a principal's effective permissions and org grants. The Core-owned port; the web
/// authorizer implements it over the Persistence authz store, matching the "store interface in
/// Persistence, seam wiring in Web" pattern.
/// </summary>
public interface IAuthzFactProvider
{
    ValueTask<AuthzPrincipalFacts> LoadFactsAsync(string userId, CancellationToken cancellationToken = default);
}
