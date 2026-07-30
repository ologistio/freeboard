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
        evaluation: all
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Collector
        id: attest-training
        title: Phishing awareness
        control: ctrl-a
        type: training
        frequency: annual
        config:
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

    // The summary must carry a count for every declared kind, so a kind dropped from the output fails
    // here rather than leaving an operator to notice the omission themselves.
    [Fact]
    public void ValidateValidConfigExitsZeroAndPrintsCounts()
    {
        var (exit, stdout, _) = CliRunner.Run("gitops", "validate", FixtureDir("valid"));

        Assert.Equal(0, exit);
        Assert.Contains("1 standard(s)", stdout, StringComparison.Ordinal);
        Assert.Contains("1 requirement(s)", stdout, StringComparison.Ordinal);
        Assert.Contains("1 control(s)", stdout, StringComparison.Ordinal);
        Assert.Contains(
            "2 asset(s) (1 company, 1 department, 0 machine, 0 vendor)", stdout, StringComparison.Ordinal);
        Assert.Contains("1 scope(s)", stdout, StringComparison.Ordinal);
        Assert.Contains("1 collector(s)", stdout, StringComparison.Ordinal);
        Assert.Contains("1 integration(s)", stdout, StringComparison.Ordinal);
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

    // The planned state must carry a section per declared kind: it is the last thing an operator reads
    // before a sync writes or hard-removes, so a kind missing from it is a silent blind spot.
    [Fact]
    public void ApplyDryRunExitsZeroAndPrintsPlannedState()
    {
        var (exit, stdout, _) = CliRunner.Run("gitops", "apply", FixtureDir("valid"), "--dry-run");

        Assert.Equal(0, exit);
        Assert.Contains("Planned config state", stdout);
        Assert.Contains("Standards (1):", stdout, StringComparison.Ordinal);
        Assert.Contains("Requirements (1):", stdout, StringComparison.Ordinal);
        Assert.Contains("Controls (1):", stdout, StringComparison.Ordinal);
        Assert.Contains("Assets (2):", stdout, StringComparison.Ordinal);
        Assert.Contains("Scopes (1):", stdout, StringComparison.Ordinal);
        Assert.Contains("Collectors (1):", stdout, StringComparison.Ordinal);
        Assert.Contains("Integrations (1):", stdout, StringComparison.Ordinal);
        Assert.Contains("std-a", stdout);
        // The whole integration line, terminator included: one assertion pins the id, title, the absent
        // vendor placeholder, provider, base URL, and cadence, and simultaneously pins that no further
        // field (a token above all) is printed. Asserting the fields separately would pass on the
        // collector line, which carries the same provider and cadence text.
        Assert.Contains(
            "  - conn-a: Connection A -> vendor - [provider fleet, https://fleet.example.com, daily]"
            + Environment.NewLine,
            stdout,
            StringComparison.Ordinal);
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

    // The four asset-edge warnings the commands must surface - a dangling parent, a dangling owner, a
    // Vendor with no owner, and a Machine with no parent - plus the one case that must stay silent. The
    // warnings are non-blocking: validation stays valid (exit 0) but the operator must still see each on
    // stderr rather than have it silently swallowed. org-root is a legitimate parentless root, so it must
    // draw nothing - a validator that warned on every parentless asset would flood stderr.
    private static string WarningsOnlyConfig() => """
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: vendor-a
        title: Vendor A
        type: Vendor
        source: declared
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: org-root
        title: Root Co
        type: Company
        source: declared
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: dept-orphan
        title: Orphan Department
        type: Department
        source: declared
        parent: ghost-parent
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: vendor-unowned
        title: Vendor with an unknown owner
        type: Vendor
        source: declared
        owner: ghost-owner
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: machine-rootless
        title: Rootless Machine
        type: Machine
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
            var (exit, stdout, stderr) = CliRunner.Run("gitops", "validate", dir);

            Assert.Equal(0, exit);
            Assert.Contains("warning:", stderr, StringComparison.Ordinal);
            Assert.Contains("vendor-a", stderr, StringComparison.Ordinal);
            // A dangling parent and a dangling owner are separate predicates: each names the asset and the
            // id nothing defines.
            Assert.Contains("dept-orphan", stderr, StringComparison.Ordinal);
            Assert.Contains("unknown parent 'ghost-parent'", stderr, StringComparison.Ordinal);
            Assert.Contains("vendor-unowned", stderr, StringComparison.Ordinal);
            Assert.Contains("unknown owner 'ghost-owner'", stderr, StringComparison.Ordinal);
            // The warnings do not suppress the success summary, and the parentless root draws nothing.
            Assert.Contains("asset(s)", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("org-root", stderr, StringComparison.Ordinal);
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
            // The missing-required-edge half of the rule: a Vendor with no owner and a Machine with no
            // parent are visible to no caller, so both warn; a parentless Company is a legitimate root.
            // Each warning is bound to its asset - the fixture also carries vendor-unowned, whose dangling
            // owner is a different predicate, so an unbound phrase would pass on the wrong row.
            Assert.Contains("'vendor-a' is a Vendor with no owner", stderr, StringComparison.Ordinal);
            Assert.Contains("'machine-rootless' is a Machine with no parent", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("org-root", stderr, StringComparison.Ordinal);
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
    public void ValidateCollectorWithUnknownControlExitsOneNamingTheControl()
    {
        var dir = WriteTempConfig("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
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
            kind: Collector
            id: ec-a
            title: Collector A
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            connection: conn-missing
            config:
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
    public void ValidateAttestationCollectorWithUnknownControlExitsOneNamingTheControl()
    {
        var dir = WriteTempConfig("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-a
            title: Template A
            control: ctrl-missing
            type: manual
            frequency: annual
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

    // Two merged-model rejections, at the command surface rather than only in Core, so a document that
    // the new model refuses is proved to stop at `gitops validate`.
    [Theory]
    [InlineData("provider: intune", "checks", "intune")]
    [InlineData("provider: fleet", "nonsense", "nonsense")]
    public void ValidateRejectsAProviderMismatchAndAnUnknownConfigKey(
        string providerLine, string configKey, string expected)
    {
        var dir = WriteTempConfig($"""
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
            kind: Integration
            id: conn-a
            title: Connection A
            provider: fleet
            base_url: https://fleet.example.com
            discovery_cadence: daily
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: ec-a
            title: Collector A
            control: ctrl-a
            type: integration
            {providerLine}
            frequency: daily
            connection: conn-a
            config:
              {configKey}:
                - source_key: "1"
                  name: check-a
                  severity: Hard
            """);
        try
        {
            var (exit, _, stderr) = CliRunner.Run("gitops", "validate", dir);

            Assert.Equal(1, exit);
            Assert.Contains(expected, stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A retired kind must stop at the command surface, not be silently ignored: the loader drops the
    // document, so only the command's exit code proves the operator is told rather than left with a
    // config that quietly lost a collector.
    [Theory]
    [InlineData("EvidenceCollector")]
    [InlineData("AttestationTemplate")]
    public void ValidateRejectsARetiredCollectorKind(string kind)
    {
        var dir = WriteTempConfig($"""
            apiVersion: freeboard.dev/v1alpha1
            kind: {kind}
            id: collector-a
            title: Collector A
            control: ctrl-a
            type: manual
            """);
        try
        {
            var (exit, _, stderr) = CliRunner.Run("gitops", "validate", dir);

            Assert.Equal(1, exit);
            Assert.Contains($"Unknown kind '{kind}'", stderr, StringComparison.Ordinal);
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
            Assert.Contains("collector(s)", validateOut);
            Assert.DoesNotContain(SentinelAnswer, validateOut, StringComparison.Ordinal);

            // apply --dry-run: the per-collector line shows identity and references only. The pass mark now
            // lives in `config`, which authoring output omits entirely along with the quiz and its answer.
            var (applyExit, applyOut, _) = CliRunner.Run("gitops", "apply", dir, "--dry-run");
            Assert.Equal(0, applyExit);
            Assert.Contains("attest-training", applyOut, StringComparison.Ordinal);
            Assert.Contains("training", applyOut, StringComparison.Ordinal);
            Assert.DoesNotContain("pass mark", applyOut, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("90%", applyOut, StringComparison.Ordinal);
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
