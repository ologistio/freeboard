using Microsoft.Extensions.Configuration;

namespace Freeboard.Compliance;

/// <summary>
/// Resolves an integration connection's API token out-of-band, by connection id. The token is never in
/// git, never in a collector config, never persisted. This seam owns the configuration key shape and
/// exposes only whether a token is present - never the value.
/// </summary>
public interface IIntegrationTokenResolver
{
    /// <summary>
    /// True when a non-blank token is configured for <paramref name="connectionId"/>. Returns only a
    /// bool: it never returns, logs, or stamps the token value.
    /// </summary>
    bool IsResolvable(string connectionId);
}

/// <summary>
/// <see cref="IConfiguration"/>-backed <see cref="IIntegrationTokenResolver"/>. The token is read from
/// <c>Freeboard:Integrations:&lt;id&gt;:ApiToken</c> (supplied by environment variables or user-secrets).
/// A value-returning method is deliberately absent: the only consumers are the read health flag and the
/// startup warning, both of which need presence, not the value; a future collection runner adds a
/// value-returning method when it exists.
/// </summary>
public sealed class IntegrationTokenResolver(IConfiguration configuration) : IIntegrationTokenResolver
{
    public bool IsResolvable(string connectionId) =>
        !string.IsNullOrWhiteSpace(configuration[$"Freeboard:Integrations:{connectionId}:ApiToken"]);
}
