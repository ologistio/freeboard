namespace Freeboard.CLI.Tests;

public sealed class GitOpsCommandTests
{
    private static string FixtureDir(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private const string SentinelAnswer = "SENTINEL-ANSWER-DO-NOT-LEAK";

    /// <summary>
    /// A valid multi-kind config with a training template whose quiz answer is a sentinel value. GitOps
    /// authoring output must never print this value.
    /// </summary>
    private static string TrainingConfigWithSentinelAnswer() => $$"""
        apiVersion: freeboard.dev/v1alpha1
        kind: Standard
        id: std-a
        title: Standard A
        version: "1.0"
        authority: Example Authority
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Requirement
        id: req-a
        title: Requirement A
        standard: std-a
        theme: Theme A
        statement: Do the thing.
        citation_label: Source A
        citation_url: https://example.com/a
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Control
        id: ctrl-a
        title: Control A
        maps_to:
          - req-a
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: AttestationTemplate
        id: attest-training
        title: Phishing awareness
        control: ctrl-a
        type: training
        pass_mark: 90
        quiz:
          - id: q1
            prompt: What should you do with an unexpected attachment?
            options: [Open it, {{SentinelAnswer}}]
            answer: {{SentinelAnswer}}
        """;

    private static string WriteTempConfig(string content)
    {
        var dir = Directory.CreateTempSubdirectory("fb-gitops-cli-");
        File.WriteAllText(Path.Join(dir.FullName, "config.yaml"), content);
        return dir.FullName;
    }

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

    // A unified Scope whose subject is a Vendor asset cannot target a standard (a Vendor scopes at the
    // requirement/control level only). This is a scope-specific validation error naming the subject and
    // exits 1. (kind: VendorScope is now an unknown kind, so this replaces the old vendor-scope case.)
    [Fact]
    public void ValidateScopeWithVendorSubjectTargetingStandardExitsOneNamingTheSubject()
    {
        var dir = WriteTempConfig("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Standard
            id: std-a
            title: Standard A
            version: "1.0"
            authority: Example Authority
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: org-a
            title: Org A
            type: Company
            source: declared
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            owner: org-a
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: vendor-a
            standard: std-a
            disposition: In
            """);
        try
        {
            var (exit, _, stderr) = CliRunner.Run("gitops", "validate", dir);

            Assert.Equal(1, exit);
            Assert.Contains("vendor-a", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A declared Vendor with no owner is a non-blocking Warning: validation stays valid (exit 0) but the
    // operator must still see the warning on stderr, not have it silently swallowed.
    private static string WarningsOnlyConfig() => """
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: vendor-a
        title: Vendor A
        type: Vendor
        source: declared
        """;

    // A Scope whose subject names no asset is a non-blocking, DB-less Core Warning: validation stays valid
    // (exit 0), but the operator must see the warning. On validate/apply --dry-run there is no database, so
    // this Core scope-subject warning prints (it is only suppressed on the sync path, in favour of the
    // importer's DB-accurate result).
    private static string DanglingScopeSubjectConfig() => """
        apiVersion: freeboard.dev/v1alpha1
        kind: Standard
        id: std-a
        title: Standard A
        version: "1.0"
        authority: Example Authority
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Requirement
        id: req-a
        title: Requirement A
        standard: std-a
        theme: Theme A
        statement: Do the thing.
        citation_label: Source A
        citation_url: https://example.com/a
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Scope
        id: scope-a
        title: Scope A
        subject: ghost-x
        requirement: req-a
        disposition: In
        """;

    [Fact]
    public void ValidatePrintsScopeSubjectWarningOnValidPathAndExitsZero()
    {
        var dir = WriteTempConfig(DanglingScopeSubjectConfig());
        try
        {
            var (exit, _, stderr) = CliRunner.Run("gitops", "validate", dir);

            Assert.Equal(0, exit);
            Assert.Contains("warning:", stderr, StringComparison.Ordinal);
            Assert.Contains("ghost-x", stderr, StringComparison.Ordinal);
            Assert.Contains("resolves to no asset", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyDryRunPrintsScopeSubjectWarningOnValidPathAndExitsZero()
    {
        var dir = WriteTempConfig(DanglingScopeSubjectConfig());
        try
        {
            var (exit, stdout, stderr) = CliRunner.Run("gitops", "apply", dir, "--dry-run");

            Assert.Equal(0, exit);
            // The planned state lists the unified scope with its subject and target.
            Assert.Contains("Scopes (1):", stdout, StringComparison.Ordinal);
            Assert.Contains("scope-a", stdout, StringComparison.Ordinal);
            Assert.Contains("ghost-x", stdout, StringComparison.Ordinal);
            Assert.Contains("resolves to no asset", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ValidatePrintsWarningsOnValidPathAndExitsZero()
    {
        var dir = WriteTempConfig(WarningsOnlyConfig());
        try
        {
            var (exit, _, stderr) = CliRunner.Run("gitops", "validate", dir);

            Assert.Equal(0, exit);
            Assert.Contains("warning:", stderr, StringComparison.Ordinal);
            Assert.Contains("vendor-a", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyDryRunPrintsWarningsOnValidPathAndExitsZero()
    {
        var dir = WriteTempConfig(WarningsOnlyConfig());
        try
        {
            var (exit, stdout, stderr) = CliRunner.Run("gitops", "apply", dir, "--dry-run");

            Assert.Equal(0, exit);
            Assert.Contains("Planned config state", stdout);
            Assert.Contains("warning:", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ValidateSummaryCountsDeclaredMachineAsset()
    {
        var dir = WriteTempConfig("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: acme
            title: Acme
            type: Company
            source: declared
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: laptop-1
            title: Laptop 1
            type: Machine
            source: declared
            parent: acme
            """);
        try
        {
            var (exit, stdout, _) = CliRunner.Run("gitops", "validate", dir);

            Assert.Equal(0, exit);
            Assert.Contains("1 machine", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ValidateEvidenceCollectorWithUnknownControlExitsOneNamingTheControl()
    {
        var dir = WriteTempConfig("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: ec-a
            title: Collector A
            control: ctrl-missing
            type: integration
            frequency: daily
            """);
        try
        {
            var (exit, _, stderr) = CliRunner.Run("gitops", "validate", dir);

            Assert.Equal(1, exit);
            Assert.Contains("ctrl-missing", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ValidateIntegrationCollectorWithUnknownConnectionExitsOneNamingTheConnection()
    {
        var dir = WriteTempConfig("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Standard
            id: std-a
            title: Standard A
            version: "1.0"
            authority: Example Authority
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Requirement
            id: req-a
            title: Requirement A
            standard: std-a
            theme: Theme A
            statement: Do the thing.
            citation_label: Source A
            citation_url: https://example.com/a
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Control
            id: ctrl-a
            title: Control A
            maps_to:
              - req-a
            evaluation: all
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: ec-a
            title: Collector A
            control: ctrl-a
            type: integration
            frequency: daily
            connection: conn-missing
            checks:
              - source_key: "1"
                name: check-a
                severity: Hard
            """);
        try
        {
            var (exit, _, stderr) = CliRunner.Run("gitops", "validate", dir);

            Assert.Equal(1, exit);
            Assert.Contains("conn-missing", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ValidateAttestationTemplateWithUnknownControlExitsOneNamingTheControl()
    {
        var dir = WriteTempConfig("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-a
            title: Template A
            control: ctrl-missing
            type: manual
            """);
        try
        {
            var (exit, _, stderr) = CliRunner.Run("gitops", "validate", dir);

            Assert.Equal(1, exit);
            Assert.Contains("ctrl-missing", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GitOpsAuthoringOutputNeverPrintsQuizAnswer()
    {
        var dir = WriteTempConfig(TrainingConfigWithSentinelAnswer());
        try
        {
            // validate: the count-only summary must not carry the answer.
            var (validateExit, validateOut, _) = CliRunner.Run("gitops", "validate", dir);
            Assert.Equal(0, validateExit);
            Assert.Contains("attestation-template(s)", validateOut);
            Assert.DoesNotContain(SentinelAnswer, validateOut, StringComparison.Ordinal);

            // apply --dry-run: the per-template line shows id/title/control/type/pass_mark, never the answer.
            var (applyExit, applyOut, _) = CliRunner.Run("gitops", "apply", dir, "--dry-run");
            Assert.Equal(0, applyExit);
            Assert.Contains("attest-training", applyOut, StringComparison.Ordinal);
            Assert.Contains("training", applyOut, StringComparison.Ordinal);
            Assert.Contains("90", applyOut, StringComparison.Ordinal);
            Assert.DoesNotContain(SentinelAnswer, applyOut, StringComparison.Ordinal);

            // sync: the success line is count-only. Without a database it exits before the line prints;
            // either way its stdout carries no answer.
            var (_, syncOut, _) = CliRunner.Run(
                "gitops", "sync", dir, "--connection-string", "Server=127.0.0.1;Port=1;Database=x;User ID=x;Password=x;");
            Assert.DoesNotContain(SentinelAnswer, syncOut, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
