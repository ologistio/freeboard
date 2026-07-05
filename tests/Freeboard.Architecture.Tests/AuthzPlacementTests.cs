using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Freeboard.CLI;
using Freeboard.Core.Authz;

namespace Freeboard.Architecture.Tests;

/// <summary>
/// Pins the authz MIT/EE placement: the pure engine and action catalog live in Freeboard.Core (MIT);
/// enforcement is web-only, so Agent and CLI wire no web authz-enforcement type (no reference to the
/// <c>Freeboard.Authz</c> namespace, which is the web project's enforcement code). The Enterprise
/// one-way rule is pinned in <see cref="EnterpriseReferenceTests"/>; here we assert Agent/CLI gain no
/// web authz enforcement. We do NOT assert the CLI output lacks the Core engine / Persistence authz
/// stores: the CLI references Persistence, so those MIT types are present transitively by design.
/// </summary>
public sealed class AuthzPlacementTests
{
    private const string WebAuthzNamespace = "Freeboard.Authz";

    [Fact]
    public void CoreAuthzEngineAndCatalogAreInCoreAssembly()
    {
        Assert.Equal("Freeboard.Core", typeof(IAuthorizationEngine).Assembly.GetName().Name);
        Assert.Equal("Freeboard.Core", typeof(AuthzActions).Assembly.GetName().Name);
        Assert.Equal("Freeboard.Core", typeof(AuthzRoles).Assembly.GetName().Name);
        Assert.Equal("Freeboard.Core", typeof(PolicyAuthorizationEngine).Assembly.GetName().Name);
    }

    [Fact]
    public void CliWiresNoWebAuthzEnforcementType()
        => AssertNoWebAuthzTypeRef(typeof(UserCommands).Assembly.Location, "CLI");

    [Fact]
    public void AgentWiresNoWebAuthzEnforcementType()
        => AssertNoWebAuthzTypeRef(AgentAssemblyPath(), "Agent");

    private static void AssertNoWebAuthzTypeRef(string assemblyPath, string label)
    {
        Assert.True(File.Exists(assemblyPath), $"assembly not found: {assemblyPath}");
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        // "Freeboard.Authz" (web enforcement) is distinct from "Freeboard.Core.Authz" (the MIT engine).
        var offenders = reader.TypeReferences
            .Select(reader.GetTypeReference)
            .Select(typeRef => reader.GetString(typeRef.Namespace))
            .Where(ns => ns == WebAuthzNamespace)
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, $"{label} references web authz enforcement namespace(s): {string.Join(", ", offenders)}");
    }

    private static string AgentAssemblyPath()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        return Path.Join(RepoRoot(), "src", "Freeboard.Agent", "bin", config, tfm, "Freeboard.Agent.dll");
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
