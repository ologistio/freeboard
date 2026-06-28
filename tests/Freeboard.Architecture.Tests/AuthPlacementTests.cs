using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Freeboard.CLI;

namespace Freeboard.Architecture.Tests;

/// <summary>
/// Pins the auth placement rules:
/// <list type="bullet">
/// <item>The CLI <c>user</c> commands administer users through the HTTP API only and do NOT
/// depend on the persistence user store. The CLI assembly references no type in the
/// <c>Freeboard.Persistence.Auth</c> namespace (the user/credential stores), yet keeps the
/// <c>Freeboard.Persistence</c> reference for <c>system migrate</c>.</item>
/// <item>The community Agent gains no auth/MFA/crypto/ULID dependency - none of Fido2,
/// Otp.NET, Konscious Argon2, or Ulid land in its build output.</item>
/// </list>
/// The Enterprise one-way rule for Persistence and CLI is pinned in
/// <see cref="EnterpriseReferenceTests"/> and <see cref="PersistencePlacementTests"/>; the
/// no-network Core structural test lives in the CLI test project.
/// </summary>
public sealed class AuthPlacementTests
{
    private const string PersistenceAuthNamespace = "Freeboard.Persistence.Auth";

    // The CLI user-administration entry point exists and is the HTTP-API command group.
    [Fact]
    public void CliExposesUserCommands()
    {
        Assert.Equal("Freeboard.CLI.UserCommands", typeof(UserCommands).FullName);
    }

    // The CLI assembly references no persistence user-store type. The user group calls the
    // HTTP API; only system migrate touches the DB, which uses the migration runner, not the auth
    // stores. A TypeRef into Freeboard.Persistence.Auth would mean the CLI bound to the user store.
    [Fact]
    public void CliReferencesNoPersistenceAuthStoreType()
    {
        var cliAssembly = typeof(UserCommands).Assembly.Location;

        using var stream = File.OpenRead(cliAssembly);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var offenders = reader.TypeReferences
            .Select(reader.GetTypeReference)
            .Select(typeRef => reader.GetString(typeRef.Namespace))
            .Where(ns => ns == PersistenceAuthNamespace
                || ns.StartsWith(PersistenceAuthNamespace + ".", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "CLI references persistence auth store namespace(s): " + string.Join(", ", offenders));
    }

    // The Agent build output ships no auth/MFA/crypto/ULID assembly.
    [Theory]
    [InlineData("Fido2")]
    [InlineData("Otp.NET")]
    [InlineData("Konscious")]
    [InlineData("Ulid")]
    public void AgentBuildOutputContainsNoAuthDependency(string assemblyPrefix)
    {
        var outputDir = AgentOutputDir();
        Assert.True(Directory.Exists(outputDir), $"build output not found: {outputDir}");

        var matches = Directory
            .EnumerateFiles(outputDir, $"{assemblyPrefix}*.dll", SearchOption.TopDirectoryOnly)
            .ToList();

        Assert.True(
            matches.Count == 0, $"Agent output contains {assemblyPrefix}: {string.Join(", ", matches)}");
    }

    private static string AgentOutputDir()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        return Path.Join(RepoRoot(), "src", "Freeboard.Agent", "bin", config, tfm);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Freeboard.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
