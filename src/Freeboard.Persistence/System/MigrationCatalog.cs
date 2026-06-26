using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Freeboard.Persistence.System;

/// <summary>
/// An embedded migration: its version stem, SQL text, parsed numeric ordinal, and
/// SHA-256 checksum.
/// </summary>
public sealed record EmbeddedMigration(string Version, int Ordinal, string Sql, string Checksum);

/// <summary>
/// Enumerates the embedded <c>Migrations/*.sql</c> resources, parses ordinals, and
/// computes checksums. Pure (no database), so the ordering and checksum logic is unit
/// testable without MySQL.
/// </summary>
public static partial class MigrationCatalog
{
    // NNN_slug.sql -> leading zero-padded ordinal then a slug.
    [GeneratedRegex(@"^(?<ord>\d+)_.+\.sql$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Loads the embedded migrations from the given assembly, ordered by parsed numeric
    /// ordinal.
    /// </summary>
    public static IReadOnlyList<EmbeddedMigration> Load(Assembly assembly)
    {
        var prefix = ResourcePrefix(assembly);

        var migrations = new List<EmbeddedMigration>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = name[prefix.Length..];
            var match = NamePattern().Match(fileName);
            if (!match.Success)
            {
                continue;
            }

            var ordinal = int.Parse(match.Groups["ord"].Value);
            var version = fileName[..^".sql".Length];
            var sql = ReadResource(assembly, name);
            migrations.Add(new EmbeddedMigration(version, ordinal, sql, Checksum(sql)));
        }

        return migrations.OrderBy(m => m.Ordinal).ToList();
    }

    private static string ResourcePrefix(Assembly assembly)
    {
        // Embedded resources are named "<rootnamespace>.Migrations.NNN_slug.sql".
        var rootNamespace = assembly.GetName().Name;
        return $"{rootNamespace}.Migrations.";
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded migration resource not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>SHA-256 of the UTF-8 SQL text as lowercase hex (64 chars).</summary>
    public static string Checksum(string sql)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sql));
        return Convert.ToHexStringLower(bytes);
    }
}
