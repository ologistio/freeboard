using System.Diagnostics;
using System.Reflection;

namespace Freeboard.CLI.Tests;

/// <summary>
/// Runs the built freeboard CLI as a child process and captures exit code and streams.
/// </summary>
internal static class CliRunner
{
    private static readonly string CliDll = LocateCliDll();

    public static (int ExitCode, string StdOut, string StdErr) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static string LocateCliDll()
    {
        // Test output: tests/Freeboard.CLI.Tests/bin/<cfg>/net10.0/
        // CLI output:  src/Freeboard.CLI/bin/<cfg>/net10.0/freeboard.dll
        var testDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var config = new DirectoryInfo(testDir).Parent!.Name; // Debug or Release
        var repoRoot = FindRepoRoot(testDir);
        var dll = Path.Combine(repoRoot, "src", "Freeboard.CLI", "bin", config, "net10.0", "freeboard.dll");
        return dll;
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Freeboard.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }
}
