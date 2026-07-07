using System.Globalization;
using Freeboard.Persistence.Auth;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Freeboard.Persistence;

/// <summary>
/// DI registration, split by role so consumers only register what they use. The web
/// app calls <see cref="AddComplianceStore"/> (reader only). The CLI calls
/// <see cref="AddGitOpsImport"/> and <see cref="AddSystemMigrations"/>. The web app calls
/// <see cref="AddAuth"/> for the full auth stack.
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

    /// <summary>
    /// Registers the connection factory and <see cref="IComplianceWriteStore"/> for
    /// app-managed writes. The web app calls this only when NOT in GitOps read-only mode.
    /// </summary>
    public static IServiceCollection AddComplianceWriteStore(this IServiceCollection services, string connectionString)
    {
        AddConnectionFactory(services, connectionString);
        services.TryAddSingleton<IComplianceWriteStore, MySqlComplianceWriteStore>();
        return services;
    }

    /// <summary>
    /// Registers the connection factory and the authz store pair (<see cref="IAuthzStore"/> read,
    /// <see cref="IAuthzAdministrationStore"/> write) as singletons through the shared connection
    /// factory, beside <see cref="AddAuth"/>. The administration store needs an
    /// <see cref="Auth.IUlidFactory"/> for audit-row ids; it is TryAdded so a co-registered
    /// <see cref="AddAuth"/> keeps its single instance.
    /// </summary>
    public static IServiceCollection AddAuthz(this IServiceCollection services, string connectionString)
    {
        AddConnectionFactory(services, connectionString);
        services.TryAddSingleton<Auth.IUlidFactory, Auth.UlidFactory>();
        services.TryAddSingleton<IAuthzStore, MySqlAuthzStore>();
        services.TryAddSingleton<IAuthzAdministrationStore, MySqlAuthzAdministrationStore>();
        return services;
    }

    /// <summary>
    /// Registers the connection factory and the read-only <see cref="IEvidenceStore"/>. Does NOT register
    /// the append store.
    /// </summary>
    public static IServiceCollection AddEvidenceStore(this IServiceCollection services, string connectionString)
    {
        AddConnectionFactory(services, connectionString);
        services.TryAddSingleton<IEvidenceStore, MySqlEvidenceStore>();
        return services;
    }

    /// <summary>
    /// Registers the connection factory and the append-only <see cref="IEvidenceWriteStore"/>. The write
    /// store needs an <see cref="Auth.IUlidFactory"/> for run/check ids; it is TryAdded so a co-registered
    /// <see cref="AddAuth"/>/<see cref="AddAuthz"/> keeps its single instance.
    /// </summary>
    public static IServiceCollection AddEvidenceWriteStore(this IServiceCollection services, string connectionString)
    {
        AddConnectionFactory(services, connectionString);
        services.TryAddSingleton<Auth.IUlidFactory, Auth.UlidFactory>();
        services.TryAddSingleton<IEvidenceWriteStore, MySqlEvidenceWriteStore>();
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

    /// <summary>
    /// Registers the connection factory ONCE plus the full auth stack: all stores, the
    /// password hasher, token hasher, secret protector, and ULID factory. Crypto material is
    /// bound from <paramref name="configuration"/> under <paramref name="cryptoSectionName"/>
    /// and validated eagerly (fail loudly on missing/weak keys). Email delivery is not part of
    /// this layer: the consuming web app configures an email transport and builds the auth
    /// messages.
    /// </summary>
    public static IServiceCollection AddAuth(
        this IServiceCollection services,
        string connectionString,
        IConfiguration configuration,
        string cryptoSectionName = "Auth")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        AddConnectionFactory(services, connectionString);

        var cryptoOptions = BindAuthCryptoOptions(configuration.GetSection(cryptoSectionName));

        // Eagerly validate ALL THREE key sets at registration so a misconfigured
        // deployment fails fast at startup, not lazily at first hash/token/decrypt. Each set
        // must be non-empty, every key at least the minimum length, and the current version
        // must name a present key.
        AuthKeyMaterial.Validate(
            cryptoOptions.PasswordSecrets, cryptoOptions.CurrentPasswordSecretVersion, "AuthCryptoOptions.PasswordSecrets");
        AuthKeyMaterial.Validate(
            cryptoOptions.TokenKeys, cryptoOptions.CurrentTokenKeyVersion, "AuthCryptoOptions.TokenKeys");
        AuthKeyMaterial.Validate(
            cryptoOptions.SecretProtectionKeys, cryptoOptions.CurrentSecretProtectionKeyVersion,
            "AuthCryptoOptions.SecretProtectionKeys", exactLength: true);

        services.TryAddSingleton(cryptoOptions);

        services.TryAddSingleton<IUlidFactory, UlidFactory>();
        services.TryAddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.TryAddSingleton<ITokenHasher, HmacTokenHasher>();
        services.TryAddSingleton<ISecretProtector, AesGcmSecretProtector>();

        services.TryAddSingleton<IUserStore, MySqlUserStore>();
        services.TryAddSingleton<IPasswordCredentialStore, MySqlPasswordCredentialStore>();
        services.TryAddSingleton<IPasswordResetStore, MySqlPasswordResetStore>();
        services.TryAddSingleton<ISessionStore, MySqlSessionStore>();
        services.TryAddSingleton<IAuthRateLimitStore, MySqlAuthRateLimitStore>();
        services.TryAddSingleton<ITotpStore, MySqlTotpStore>();
        services.TryAddSingleton<IRecoveryCodeStore, MySqlRecoveryCodeStore>();
        services.TryAddSingleton<IWebAuthnCredentialStore, MySqlWebAuthnCredentialStore>();
        services.TryAddSingleton<IMfaChallengeStore, MySqlMfaChallengeStore>();

        return services;
    }

    /// <summary>
    /// Builds <see cref="AuthCryptoOptions"/> from config. Each versioned key set is a child
    /// section of <c>version -> base64-key</c> entries; the current version is a sibling int.
    /// Keys are REQUIRED out-of-band material (env/user-secrets/config), so a missing section
    /// is a hard error. Strength is validated downstream by each component at construction.
    /// </summary>
    private static AuthCryptoOptions BindAuthCryptoOptions(IConfigurationSection section)
    {
        return new AuthCryptoOptions
        {
            PasswordSecrets = ReadKeySet(section, "PasswordSecrets"),
            CurrentPasswordSecretVersion = ReadCurrentVersion(section, "CurrentPasswordSecretVersion"),
            TokenKeys = ReadKeySet(section, "TokenKeys"),
            CurrentTokenKeyVersion = ReadCurrentVersion(section, "CurrentTokenKeyVersion"),
            SecretProtectionKeys = ReadKeySet(section, "SecretProtectionKeys"),
            CurrentSecretProtectionKeyVersion = ReadCurrentVersion(section, "CurrentSecretProtectionKeyVersion"),
        };
    }

    private static IReadOnlyDictionary<int, byte[]> ReadKeySet(IConfigurationSection parent, string name)
    {
        var section = parent.GetSection(name);
        var keys = new Dictionary<int, byte[]>();
        foreach (var child in section.GetChildren())
        {
            if (!int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException(
                    $"{parent.Path}:{name} has a non-integer version key '{child.Key}'.");
            }

            if (string.IsNullOrEmpty(child.Value))
            {
                throw new InvalidOperationException($"{parent.Path}:{name}:{child.Key} is empty.");
            }

            keys[version] = Convert.FromBase64String(child.Value);
        }

        if (keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"{parent.Path}:{name} is missing. It is REQUIRED and must be supplied out-of-band (env/user-secrets/config).");
        }

        return keys;
    }

    private static int ReadCurrentVersion(IConfigurationSection parent, string name)
    {
        var value = parent[name];
        if (string.IsNullOrEmpty(value)
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new InvalidOperationException($"{parent.Path}:{name} is missing or not an integer.");
        }

        return version;
    }

    private static void AddConnectionFactory(IServiceCollection services, string connectionString)
    {
        services.TryAddPersistenceOptions(connectionString);
        // TryAdd so calling more than one Add* extension (e.g. AddComplianceStore +
        // AddAuth on the web app) registers the factory exactly once instead of duplicating it.
        services.TryAddSingleton<IDbConnectionFactory, MySqlConnectionFactory>();
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
