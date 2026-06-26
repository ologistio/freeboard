namespace Freeboard.CLI.Tests;

public sealed class GitOpsCommandTests
{
    private static string FixtureDir(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void ValidateValidConfigExitsZeroAndPrintsCounts()
    {
        var (exit, stdout, _) = CliRunner.Run("gitops", "validate", FixtureDir("valid"));

        Assert.Equal(0, exit);
        Assert.Contains("standard(s)", stdout);
    }

    [Fact]
    public void ValidateInvalidConfigExitsOneWithErrorsOnStderr()
    {
        var (exit, _, stderr) = CliRunner.Run("gitops", "validate", FixtureDir("invalid"));

        Assert.Equal(1, exit);
        Assert.Contains("std-missing", stderr);
    }

    [Fact]
    public void ValidateMissingPathExitsOne()
    {
        var (exit, _, stderr) = CliRunner.Run("gitops", "validate", "/no/such/dir/x");

        Assert.Equal(1, exit);
        Assert.Contains("not found", stderr);
    }

    [Fact]
    public void ApplyDryRunExitsZeroAndPrintsPlannedState()
    {
        var (exit, stdout, _) = CliRunner.Run("gitops", "apply", FixtureDir("valid"), "--dry-run");

        Assert.Equal(0, exit);
        Assert.Contains("Planned config state", stdout);
        Assert.Contains("std-a", stdout);
    }

    [Fact]
    public void ApplyWithoutDryRunExitsTwoWithDeferralOnStderr()
    {
        var (exit, _, stderr) = CliRunner.Run("gitops", "apply", FixtureDir("valid"));

        Assert.Equal(2, exit);
        Assert.Contains("dry-run", stderr);
        Assert.Contains("later increment", stderr);
    }

    [Fact]
    public void ApplyDryRunOnInvalidConfigExitsOne()
    {
        var (exit, stdout, stderr) = CliRunner.Run("gitops", "apply", FixtureDir("invalid"), "--dry-run");

        Assert.Equal(1, exit);
        Assert.Contains("std-missing", stderr);
        Assert.DoesNotContain("Planned config state", stdout);
    }
}
