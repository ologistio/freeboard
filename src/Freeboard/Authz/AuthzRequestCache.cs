using Freeboard.Core.Authz;
using Freeboard.Persistence;

namespace Freeboard.Authz;

/// <summary>
/// The ONE request-scoped cache shared by the authorizer and <c>AuthzOrgAccess</c>: a principal's
/// facts and the organisation tree load at most once per request and never leak across requests
/// (registered SCOPED). Implements <see cref="IAuthzFactProvider"/> so it is the single fact loader.
/// </summary>
public sealed class AuthzRequestCache(IAuthzStore store, IComplianceStore compliance) : IAuthzFactProvider
{
    private readonly Dictionary<string, AuthzPrincipalFacts> _facts = new(StringComparer.Ordinal);
    private IReadOnlyList<OrganisationRow>? _organisations;

    public async ValueTask<AuthzPrincipalFacts> LoadFactsAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        if (_facts.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        var facts = await store.LoadPrincipalFactsAsync(userId, cancellationToken).ConfigureAwait(false);
        _facts[userId] = facts;
        return facts;
    }

    public async ValueTask<IReadOnlyList<OrganisationRow>> GetOrganisationsAsync(
        CancellationToken cancellationToken = default)
        => _organisations ??= await compliance.GetOrganisationsAsync(cancellationToken).ConfigureAwait(false);
}
