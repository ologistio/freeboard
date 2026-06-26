using ConsoleAppFramework;
using Freeboard.Core.GitOps;

namespace Freeboard.CLI;

/// <summary>
/// The <c>gitops</c> command group. Loads and validates declarative compliance
/// config from a directory. Makes no network calls and writes no state.
/// </summary>
public sealed class GitOpsCommands
{
    /// <summary>Validate a GitOps config directory.</summary>
    /// <param name="path">Directory containing the YAML config.</param>
    public int Validate([Argument] string path)
    {
        var result = ConfigValidator.LoadAndValidate(path);
        if (!result.IsValid)
        {
            PrintDiagnostics(result);
            return 1;
        }

        PrintSummary(result.Config);
        return 0;
    }

    /// <summary>Show the config state that would be applied (dry-run only).</summary>
    /// <param name="path">Directory containing the YAML config.</param>
    /// <param name="dryRun">Required in this version. Real apply lands in a later increment.</param>
    public int Apply([Argument] string path, bool dryRun = false)
    {
        if (!dryRun)
        {
            Console.Error.WriteLine(
                "Only 'apply --dry-run' is supported in this version. Real apply lands in a later increment.");
            return 2;
        }

        var result = ConfigValidator.LoadAndValidate(path);
        if (!result.IsValid)
        {
            PrintDiagnostics(result);
            return 1;
        }

        Console.WriteLine("Planned config state (dry-run, nothing written):");
        PrintPlannedState(result.Config);
        return 0;
    }

    private static void PrintDiagnostics(ConfigResult result)
    {
        foreach (var diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(diagnostic.ToString());
        }
    }

    private static void PrintSummary(GitOpsConfig config)
    {
        Console.WriteLine(
            $"OK: {config.Standards.Count} standard(s), {config.Controls.Count} control(s), {config.Scopes.Count} scope(s).");
    }

    private static void PrintPlannedState(GitOpsConfig config)
    {
        Console.WriteLine($"Standards ({config.Standards.Count}):");
        foreach (var standard in config.Standards)
        {
            Console.WriteLine($"  - {standard.Id}: {standard.Title}");
        }

        Console.WriteLine($"Controls ({config.Controls.Count}):");
        foreach (var control in config.Controls)
        {
            Console.WriteLine($"  - {control.Id}: {control.Title} -> [{string.Join(", ", control.MapsTo)}]");
        }

        Console.WriteLine($"Scopes ({config.Scopes.Count}):");
        foreach (var scope in config.Scopes)
        {
            Console.WriteLine($"  - {scope.Id}: {scope.Title} -> [{string.Join(", ", scope.Controls)}]");
        }
    }
}
