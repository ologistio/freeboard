using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Freeboard.Persistence;

/// <summary>
/// DI registration, split by role so consumers only register what they use. The web
/// app calls <see cref="AddComplianceStore"/> (reader only). The CLI calls
/// <see cref="AddGitOpsImport"/> and <see cref="AddSystemMigrations"/>.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the connection factory and <see cref="IComplianceStore"/> only. Does
    /// NOT register the importer or migration runner.
    /// </summary>
    public static IServiceCollection AddComplianceStore(this IServiceCollection services, string connectionString)
    {
        AddConnectionFactory(services, connectionString);
        services.AddSingleton<IComplianceStore, MySqlComplianceStore>();
        return services;
    }

    /// <summary>Registers the connection factory and <see cref="IGitOpsImporter"/>.</summary>
    public static IServiceCollection AddGitOpsImport(this IServiceCollection services, string connectionString)
    {
        AddConnectionFactory(services, connectionString);
        services.AddSingleton<IGitOpsImporter, MySqlGitOpsImporter>();
        return services;
    }

    /// <summary>Registers the connection factory and <see cref="IMigrationRunner"/>.</summary>
    public static IServiceCollection AddSystemMigrations(this IServiceCollection services, string connectionString)
    {
        AddConnectionFactory(services, connectionString);
        services.AddSingleton<IMigrationRunner, MySqlMigrationRunner>();
        return services;
    }

    private static void AddConnectionFactory(IServiceCollection services, string connectionString)
    {
        services.TryAddPersistenceOptions(connectionString);
        services.AddSingleton<IDbConnectionFactory, MySqlConnectionFactory>();
    }

    private static void TryAddPersistenceOptions(this IServiceCollection services, string connectionString)
    {
        // The same DB backs the store, importer, and migrations; register the options
        // once even if multiple Add* extensions are called. TryAdd keeps the first
        // registration so a later Add* with a different connection string cannot
        // last-wins and silently swap the connection.
        services.TryAddSingleton(new PersistenceOptions { ConnectionString = connectionString });
    }
}
