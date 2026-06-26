using System.Xml.Linq;

namespace Freeboard.Architecture.Tests;

/// <summary>
/// Pins the persistence placement: the new MIT persistence project carries no
/// Enterprise reference; the Agent stays free of persistence and the DB client; and
/// Core gains no persistence/DB dependency. The csproj parse is the load-bearing
/// check, mirroring EnterpriseReferenceTests.
/// </summary>
public sealed class PersistencePlacementTests
{
    private const string Enterprise = "Freeboard.Enterprise";
    private const string Persistence = "Freeboard.Persistence";

    // 8.1
    [Fact]
    public void PersistenceDoesNotReferenceEnterprise()
    {
        var refs = ProjectReferences("Freeboard.Persistence");
        Assert.DoesNotContain(refs, r => r.Contains(Enterprise, StringComparison.OrdinalIgnoreCase));
    }

    // 8.2
    [Theory]
    [InlineData(Persistence)]
    [InlineData("MySqlConnector")]
    [InlineData("Dapper")]
    public void AgentDoesNotReferencePersistenceOrDbClients(string forbidden)
    {
        var refs = ProjectReferences("Freeboard.Agent");
        Assert.DoesNotContain(refs, r => r.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    // 8.2 - the Agent build output ships no persistence or DB-client assembly.
    [Theory]
    [InlineData("Freeboard.Persistence")]
    [InlineData("MySqlConnector")]
    [InlineData("Dapper")]
    public void AgentBuildOutputContainsNoPersistenceOrDbAssembly(string assemblyName)
    {
        var outputDir = ProjectOutputDir("Freeboard.Agent");
        Assert.True(Directory.Exists(outputDir), $"build output not found: {outputDir}");

        var matches = Directory
            .EnumerateFiles(outputDir, $"{assemblyName}*.dll", SearchOption.TopDirectoryOnly)
            .ToList();

        Assert.True(matches.Count == 0, $"Agent output contains {assemblyName}: {string.Join(", ", matches)}");
    }

    // 8.3
    [Fact]
    public void CoreDoesNotReferencePersistence()
    {
        var refs = ProjectReferences("Freeboard.Core");
        Assert.DoesNotContain(refs, r => r.Contains(Persistence, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ProjectReferences(string projectName)
    {
        var csproj = Path.Join(RepoRoot(), "src", projectName, $"{projectName}.csproj");
        Assert.True(File.Exists(csproj), $"csproj not found: {csproj}");

        var doc = XDocument.Load(csproj);
        return doc.Descendants()
            .Where(e => e.Name.LocalName is "ProjectReference" or "Reference" or "PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .ToList();
    }

    private static string ProjectOutputDir(string projectName)
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        return Path.Join(RepoRoot(), "src", projectName, "bin", config, tfm);
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
