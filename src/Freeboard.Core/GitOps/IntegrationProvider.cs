namespace Freeboard.Core.GitOps;

/// <summary>
/// The canonical integration-connection provider vocabulary. Owns the closed provider token set
/// (reused by <see cref="ConfigValidator"/> and the future discovery/collection runner). A provider
/// selects the runner/adapter and is the axis that aligns a connection with the machines a source
/// reports: a machine's <c>asset_source.source</c> token equals the connection's provider token. The
/// only V1 value is <c>fleet</c>.
/// </summary>
public static class IntegrationProvider
{
    /// <summary>Closed token set for a connection's provider (case-sensitive).</summary>
    public static readonly IReadOnlySet<string> Tokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "fleet",
    };
}
