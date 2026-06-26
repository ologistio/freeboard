namespace Freeboard.Persistence;

/// <summary>
/// Persistence configuration. Holds the MySQL connection string. The connection
/// string is a secret and is supplied via environment variables, user-secrets, or
/// a config provider - never committed config or GitOps YAML.
/// </summary>
public sealed class PersistenceOptions
{
    public required string ConnectionString { get; init; }
}
