using System.Xml.Linq;

namespace Freeboard.Architecture.Tests;

/// <summary>
/// Pins the EE one-way rule: the community projects (Core, CLI, Agent) must carry
/// no reference to Freeboard.Enterprise. The csproj parse is the load-bearing check:
/// the compiler elides unused managed references, so GetReferencedAssemblies() does
/// not catch an unused ProjectReference and would let an Enterprise.dll ship next to
/// an MIT binary. So we also assert no Freeboard.Enterprise.* assembly lands in each
/// project's build output.
/// </summary>
public sealed class EnterpriseReferenceTests
{
    private const string Enterprise = "Freeboard.Enterprise";

    [Theory]
    [InlineData("Freeboard.Core")]
    [InlineData("Freeboard.CLI")]
    [InlineData("Freeboard.Agent")]
    public void CommunityCsprojDoesNotReferenceEnterprise(string projectName)
    {
        if (Path.IsPathRooted(projectName)
            || projectName.Contains(Path.DirectorySeparatorChar)
            || projectName.Contains(Path.AltDirectorySeparatorChar))
        {
            Assert.Fail($"projectName must be a simple folder name, not a rooted or nested path: {projectName}");
        }

        var csproj = Path.Combine(RepoRoot(), "src", projectName, $"{projectName}.csproj");
        Assert.True(File.Exists(csproj), $"csproj not found: {csproj}");

        var doc = XDocument.Load(csproj);

        var references = doc.Descendants()
            .Where(e => e.Name.LocalName is "ProjectReference" or "Reference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty);

        Assert.DoesNotContain(references, include => include.Contains(Enterprise, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Freeboard.Core")]
    [InlineData("Freeboard.CLI")]
    [InlineData("Freeboard.Agent")]
    public void CommunityBuildOutputContainsNoEnterpriseAssembly(string projectName)
    {
        var outputDir = ProjectOutputDir(projectName);
        Assert.True(Directory.Exists(outputDir), $"build output not found: {outputDir}");

        var enterpriseAssemblies = Directory
            .EnumerateFiles(outputDir, $"{Enterprise}*.dll", SearchOption.TopDirectoryOnly)
            .ToList();

        Assert.True(
            enterpriseAssemblies.Count == 0,
            $"{projectName} build output contains Enterprise assembly: {string.Join(", ", enterpriseAssemblies)}");
    }

    [Fact]
    public void EnterpriseCsprojReferencesOnlyCore()
    {
        if (Path.IsPathRooted(Enterprise)
            || Enterprise.Contains(Path.DirectorySeparatorChar)
            || Enterprise.Contains(Path.AltDirectorySeparatorChar))
        {
            Assert.Fail($"Enterprise must be a simple folder name, not a rooted or nested path: {Enterprise}");
        }

        var csproj = Path.Combine(RepoRoot(), "src", Enterprise, $"{Enterprise}.csproj");
        Assert.True(File.Exists(csproj), $"csproj not found: {csproj}");

        var references = XDocument.Load(csproj).Descendants()
            .Where(e => e.Name.LocalName is "ProjectReference" or "Reference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .ToList();

        // The one-way rule forward direction: Enterprise may reference Core and nothing else (no web,
        // no persistence), so the paid catalog cannot pull in web/persistence types.
        var projectRef = Assert.Single(references);
        Assert.Contains("Freeboard.Core", projectRef, StringComparison.Ordinal);
    }

    // Mirror the build configuration/framework of the test assembly so the sibling
    // project output directory is found regardless of Debug/Release.
    private static string ProjectOutputDir(string projectName)
    {
        // .../src/<project>/bin/<config>/<tfm>/
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        return Path.Combine(RepoRoot(), "src", projectName, "bin", config, tfm);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Freeboard.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
