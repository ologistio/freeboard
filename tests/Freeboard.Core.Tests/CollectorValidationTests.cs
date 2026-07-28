using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the unified Collector kind and the Control evaluation rule: kind routing, required fields
/// (frequency on every type), resolvable control/vendor references, the type and frequency token sets,
/// the optional threshold and config pass_mark range checks (a malformed value is a diagnostic, not a
/// crash), the type-conditional provider/connection rules and the provider cross-check, the form, quiz,
/// and tracked-check shape rules read from config, duplicate ids, unknown top-level fields (including
/// the pre-merge payload fields a half-migrated document carries), the evaluation enum and the
/// evaluation-required-when-collectors rule, and the config normalization. The loader and validator
/// never throw or print.
/// </summary>
public sealed class CollectorValidationTests
{
    private const string ValidStandard = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Standard
        id: std-a
        title: Standard A
        version: "1.0"
        authority: Example Authority
        """;

    private const string ValidRequirement = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Requirement
        id: req-a
        title: Requirement A
        standard: std-a
        theme: Theme A
        statement: Do the thing.
        citation_label: Source A
        citation_url: https://example.com/a
        """;

    private const string ControlWithEvaluation = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Control
        id: ctrl-a
        title: Control A
        maps_to:
          - req-a
        evaluation: all
        """;

    private const string ControlWithoutEvaluation = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Control
        id: ctrl-a
        title: Control A
        maps_to:
          - req-a
        """;

    private const string ValidOwnerCompany = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: org-a
        title: Org A
        type: Company
        source: declared
        """;

    private const string ValidVendor = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: vendor-a
        title: Vendor A
        type: Vendor
        source: declared
        owner: org-a
        """;

    private const string ValidConnection = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Integration
        id: fleet-prod
        title: Fleet Production
        provider: fleet
        base_url: https://fleet.example.com
        discovery_cadence: daily
        vendor: vendor-a
        """;

    private static string ValidSet(string collector, string control = ControlWithEvaluation) =>
        $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{control}\n---\n{ValidOwnerCompany}\n---\n{ValidVendor}\n"
        + $"---\n{ValidConnection}\n---\n{collector}";

    [Fact]
    public void CollectorLoadsAttachedToControlWithOptionalFields()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: Endpoint MFA via Fleet
            control: ctrl-a
            vendor: vendor-a
            type: integration
            provider: fleet
            frequency: daily
            threshold: 100
            connection: fleet-prod
            config:
              checks:
                - source_key: "12"
                  name: mfa-enforced
                  severity: Hard
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var collector = Assert.Single(result.Config.Collectors);
        Assert.Equal("collector-a", collector.Id);
        Assert.Equal("ctrl-a", collector.Control);
        Assert.Equal("vendor-a", collector.Vendor);
        Assert.Equal("integration", collector.Type);
        Assert.Equal("fleet", collector.Provider);
        Assert.Equal("daily", collector.Frequency);
        Assert.Equal("100", collector.Threshold);
        Assert.Equal("fleet-prod", collector.Connection);
        var check = Assert.Single(collector.Config.Checks);
        Assert.Equal(("12", "mfa-enforced", "Hard"), (check.SourceKey, check.Name, check.Severity));
    }

    [Fact]
    public void ControlLoadsWithEvaluationRule()
    {
        using var dir = TempConfig.Create(("c.yaml", ControlWithEvaluation));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("all", Assert.Single(result.Config.Controls).Evaluation);
    }

    [Fact]
    public void ValidCollectorPassesValidation()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void OptionalFieldsOmittedStillValid()
    {
        // No vendor, no threshold, no config: all optional, and (script, -) registers no required key.
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: weekly
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var collector = Assert.Single(result.Config.Collectors);
        Assert.Equal(string.Empty, collector.Vendor);
        Assert.Equal(string.Empty, collector.Threshold);
        Assert.Equal(string.Empty, collector.Provider);
        Assert.Equal(string.Empty, collector.Connection);
        Assert.Equal(string.Empty, collector.Config.Body);
        Assert.Empty(collector.Config.Fields);
        Assert.Empty(collector.Config.Quiz);
        Assert.Empty(collector.Config.Checks);
    }

    [Fact]
    public void UnknownFieldIsRejected()
    {
        using var dir = TempConfig.Create(("c.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: daily
            organisation: org-a
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'organisation'") && d.Message.Contains("Collector"));
    }

    // The five pre-merge top-level payload fields are the shape a half-migrated EvidenceCollector or
    // AttestationTemplate document produces, so each must fail rather than be silently ignored.
    [Theory]
    [InlineData("checks:\n  - source_key: \"12\"\n    name: n\n    severity: Hard")]
    [InlineData("body: Confirm the ruleset was reviewed.")]
    [InlineData("fields:\n  - id: f1\n    label: L\n    type: boolean")]
    [InlineData("pass_mark: 80")]
    [InlineData("quiz:\n  - id: q1\n    prompt: P\n    options: [a, b]\n    answer: a")]
    public void PreMergeTopLevelPayloadFieldIsRejected(string payload)
    {
        using var dir = TempConfig.Create(("c.yaml", $"""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            {payload}
            """));

        var result = ConfigLoader.Load(dir.Path);

        var key = payload[..payload.IndexOf(':', StringComparison.Ordinal)];
        Assert.Contains(result.Diagnostics, d => d.Message.Contains($"Unknown field '{key}'") && d.Message.Contains("Collector"));
    }

    [Fact]
    public void MissingControlFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            type: script
            frequency: daily
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("'control'"));
    }

    [Fact]
    public void MissingTypeFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            frequency: daily
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("'type'"));
    }

    // frequency is required on every type, attestations included.
    [Theory]
    [InlineData("script")]
    [InlineData("manual")]
    [InlineData("training")]
    public void MissingFrequencyFails(string type)
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet($"""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: {type}
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("'frequency'"));
    }

    [Fact]
    public void UnknownTypeFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: webhook
            frequency: daily
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("unknown type 'webhook'"));
    }

    [Theory]
    [InlineData("manual-attestation")]
    [InlineData("training-attestation")]
    public void RetiredAttestationTypeTokenFails(string type)
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet($"""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: {type}
            frequency: annual
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains($"unknown type '{type}'"));
    }

    [Fact]
    public void UnknownFrequencyFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: hourly
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("unknown frequency 'hourly'"));
    }

    [Fact]
    public void NonIntegerThresholdIsDiagnosticNotCrash()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: daily
            threshold: high
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("invalid threshold 'high'"));
    }

    [Fact]
    public void OutOfRangeThresholdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: daily
            threshold: 150
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("invalid threshold '150'"));
    }

    [Fact]
    public void UnknownControlReferenceFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-missing
            type: script
            frequency: daily
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("unknown Control id 'ctrl-missing'"));
    }

    [Fact]
    public void UnknownVendorReferenceFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            vendor: vendor-missing
            type: script
            frequency: daily
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("unknown Vendor id 'vendor-missing'"));
    }

    [Fact]
    public void DuplicateCollectorIdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            {ValidRequirement}
            ---
            {ControlWithEvaluation}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: daily
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: agent
            frequency: weekly
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate Collector id 'collector-a'"));
    }

    // The id space collapses with the kinds, and the case above does not show it: two script
    // collectors collided before the merge as well. A collector shape and an attestation shape
    // sharing an id did NOT, because duplicate-id detection ran per kind. That pair validates on
    // the pre-merge model and is rejected here, so it is pinned as the verdict change it is.
    [Fact]
    public void ACollectorAndAnAttestationSharingAnIdNowCollide()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            {ValidRequirement}
            ---
            {ControlWithEvaluation}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: shared-id
            title: T
            control: ctrl-a
            type: script
            frequency: daily
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: shared-id
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate Collector id 'shared-id'"));
    }

    [Fact]
    public void WrongApiVersionFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v2
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: daily
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("unknown apiVersion"));
    }

    [Fact]
    public void UnknownControlEvaluationFails()
    {
        using var dir = TempConfig.Create(("c.yaml", $"{ValidStandard}\n---\n{ValidRequirement}\n---\n" + """
            apiVersion: freeboard.dev/v1alpha1
            kind: Control
            id: ctrl-a
            title: Control A
            maps_to:
              - req-a
            evaluation: majority
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("ctrl-a") && d.Message.Contains("unknown evaluation 'majority'"));
    }

    [Fact]
    public void ControlWithCollectorAndNoEvaluationFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: daily
            """, ControlWithoutEvaluation)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("ctrl-a") && d.Message.Contains("missing required field 'evaluation'"));
    }

    // A control whose only proving mechanism is an attestation now needs an evaluation rule too. The
    // pre-merge AttestationTemplate never recorded a control as having something attached, so this is a
    // deliberate widening rather than an accident of the merge.
    [Theory]
    [InlineData("manual")]
    [InlineData("training")]
    public void ControlProvedOnlyByAnAttestationRequiresEvaluation(string type)
    {
        var form = type == "training"
            ? """
                config:
                  pass_mark: 80
                  quiz:
                    - id: q1
                      prompt: P
                      options: [a, b]
                      answer: a
                """
            : string.Empty;
        using var dir = TempConfig.Create(("all.yaml", ValidSet($"""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: {type}
            frequency: annual
            {form}
            """, ControlWithoutEvaluation)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("ctrl-a") && d.Message.Contains("missing required field 'evaluation'"));
    }

    [Fact]
    public void ControlWithCollectorAndEvaluationPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: daily
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void ControlWithNoCollectorAndNoEvaluationPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{ControlWithoutEvaluation}"));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void CollectorNamingMissingControlYieldsOnlyUnknownControlDiagnostic()
    {
        // The collector names a control no document defines. Only the unknown-control diagnostic must
        // appear; the missing-evaluation check must NOT fire for the undefined id.
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-ghost
            type: script
            frequency: daily
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown Control id 'ctrl-ghost'"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("ctrl-ghost") && d.Message.Contains("evaluation"));
    }

    [Fact]
    public void IntegrationCollectorMissingProviderFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            frequency: daily
            connection: fleet-prod
            config:
              checks:
                - source_key: "12"
                  name: mfa-enforced
                  severity: Hard
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("missing required field 'provider'"));
    }

    [Fact]
    public void IntegrationCollectorUnknownProviderFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: intune
            frequency: daily
            connection: fleet-prod
            config:
              checks:
                - source_key: "12"
                  name: mfa-enforced
                  severity: Hard
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("unknown provider 'intune'"));
    }

    [Fact]
    public void ProviderDisagreeingWithTheConnectionFails()
    {
        // The duplication between the authored provider and the connection's is bounded by this
        // cross-check, so the two can never disagree silently. Only one provider token is registered in
        // this increment, so the connection is the side carrying the other value.
        var connection = """
            apiVersion: freeboard.dev/v1alpha1
            kind: Integration
            id: fleet-prod
            title: Fleet Production
            provider: crowdstrike
            base_url: https://fleet.example.com
            discovery_cadence: daily
            """;
        using var dir = TempConfig.Create(("all.yaml",
            $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{ControlWithEvaluation}\n---\n{connection}\n---\n" + """
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            connection: fleet-prod
            config:
              checks:
                - source_key: "12"
                  name: mfa-enforced
                  severity: Hard
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d =>
            d.Message.Contains("collector-a")
            && d.Message.Contains("has provider 'fleet'")
            && d.Message.Contains("'fleet-prod' has provider 'crowdstrike'"));
    }

    [Fact]
    public void ConnectionWithNoProviderIsReportedOnceNotAsAMismatchToo()
    {
        // A connection missing its provider is one authoring mistake. The cross-check stays quiet rather
        // than adding a mismatch naming the connection's empty value.
        var connection = """
            apiVersion: freeboard.dev/v1alpha1
            kind: Integration
            id: fleet-prod
            title: Fleet Production
            base_url: https://fleet.example.com
            discovery_cadence: daily
            """;
        using var dir = TempConfig.Create(("all.yaml",
            $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{ControlWithEvaluation}\n---\n{connection}\n---\n" + """
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            connection: fleet-prod
            config:
              checks:
                - source_key: "12"
                  name: mfa-enforced
                  severity: Hard
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.Contains(result.Diagnostics, d =>
            d.Message.Contains("fleet-prod") && d.Message.Contains("missing required field 'provider'"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("but its Integration"));
    }

    [Theory]
    [InlineData("provider: fleet")]
    [InlineData("connection: fleet-prod")]
    public void ProviderOrConnectionOnNonIntegrationCollectorFails(string field)
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet($"""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: weekly
            {field}
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        var key = field[..field.IndexOf(':', StringComparison.Ordinal)];
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains($"declares '{key}'"));
    }

    [Fact]
    public void ManualCollectorLoadsWithFieldsInConfig()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-manual
            title: Firewall change attestation
            control: ctrl-a
            type: manual
            frequency: quarterly
            config:
              body: Confirm the ruleset was reviewed.
              fields:
                - id: reviewed
                  label: Ruleset reviewed?
                  type: boolean
                - id: outcome
                  label: Review outcome
                  type: single-choice
                  options: [pass, pass-with-notes, fail]
                - id: notes
                  label: Notes
                  type: short-text
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var collector = Assert.Single(result.Config.Collectors);
        Assert.Equal("manual", collector.Type);
        Assert.Equal("Confirm the ruleset was reviewed.", collector.Config.Body);
        Assert.Equal(3, collector.Config.Fields.Count);
        Assert.Equal(["pass", "pass-with-notes", "fail"], collector.Config.Fields[1].Options.ToArray());
        Assert.Empty(collector.Config.Checks);
    }

    [Fact]
    public void TrainingCollectorLoadsWithQuizAndPassMarkInConfig()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-training
            title: Phishing awareness
            control: ctrl-a
            type: training
            frequency: annual
            config:
              body: Read the guidance, then answer.
              pass_mark: 80
              quiz:
                - id: q1
                  prompt: What should you do with an unexpected attachment?
                  options: [Open it, Report it, Forward it]
                  answer: Report it
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var collector = Assert.Single(result.Config.Collectors);
        Assert.Equal("training", collector.Type);
        Assert.Equal("80", collector.Config.PassMark);
        var item = Assert.Single(collector.Config.Quiz);
        Assert.Equal("q1", item.Id);
        Assert.Equal("Report it", item.Answer);
    }

    // The manual/training parity the merge accepts as a criterion: every (manual, -) key is optional, so
    // a manual collector authoring none of them, or only a body, still validates.
    [Fact]
    public void ManualCollectorWithConfigOmittedValidates()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var collector = Assert.Single(result.Config.Collectors);
        Assert.Equal(string.Empty, collector.Config.Body);
        Assert.Empty(collector.Config.Fields);
        Assert.Equal(string.Empty, collector.Config.PassMark);
        Assert.Empty(collector.Config.Quiz);
    }

    [Fact]
    public void ManualCollectorWithBodyButNoFieldsValidates()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            config:
              body: Confirm the ruleset was reviewed.
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void TrainingCollectorWithFieldsValidatesAndAppliesTheFormFieldRules()
    {
        // `fields` is registered on training too, so authoring it alongside pass_mark/quiz is accepted -
        // and the same form-field rules apply to it as to a manual collector's.
        var withValidField = ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            frequency: annual
            config:
              pass_mark: 80
              fields:
                - id: f1
                  label: L
                  type: boolean
              quiz:
                - id: q1
                  prompt: P
                  options: [a, b]
                  answer: a
            """);
        using var dir = TempConfig.Create(("all.yaml", withValidField));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));

        using var badDir = TempConfig.Create(("all.yaml", withValidField.Replace("type: boolean", "type: rating", StringComparison.Ordinal)));

        var badResult = ConfigValidator.LoadAndValidate(badDir.Path);

        Assert.False(badResult.IsValid);
        Assert.Contains(badResult.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("unknown type 'rating'"));
    }

    [Fact]
    public void UnknownFieldTypeFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            config:
              fields:
                - id: f1
                  label: L
                  type: rating
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("unknown type 'rating'"));
    }

    [Fact]
    public void SingleChoiceWithFewerThanTwoOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            config:
              fields:
                - id: f1
                  label: L
                  type: single-choice
                  options: [only]
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("fewer than two options"));
    }

    [Fact]
    public void SingleChoiceWithDuplicateOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            config:
              fields:
                - id: f1
                  label: L
                  type: single-choice
                  options: [pass, pass]
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("duplicate option 'pass'"));
    }

    [Fact]
    public void NonChoiceFieldWithOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            config:
              fields:
                - id: f1
                  label: L
                  type: boolean
                  options: [yes, no]
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("declares options"));
    }

    [Fact]
    public void DuplicateFieldIdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            config:
              fields:
                - id: dup
                  label: A
                  type: boolean
                - id: dup
                  label: B
                  type: short-text
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("duplicate field id 'dup'"));
    }

    [Fact]
    public void NonIntegerPassMarkIsDiagnosticNotCrash()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            frequency: annual
            config:
              pass_mark: high
              quiz:
                - id: q1
                  prompt: P
                  options: [a, b]
                  answer: a
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("invalid pass_mark 'high'"));
    }

    [Fact]
    public void OutOfRangePassMarkFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            frequency: annual
            config:
              pass_mark: 150
              quiz:
                - id: q1
                  prompt: P
                  options: [a, b]
                  answer: a
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("invalid pass_mark '150'"));
    }

    [Fact]
    public void DuplicateQuizIdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            frequency: annual
            config:
              pass_mark: 50
              quiz:
                - id: dup
                  prompt: P1
                  options: [a, b]
                  answer: a
                - id: dup
                  prompt: P2
                  options: [c, d]
                  answer: c
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("duplicate quiz id 'dup'"));
    }

    [Fact]
    public void QuizItemWithFewerThanTwoOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            frequency: annual
            config:
              pass_mark: 50
              quiz:
                - id: q1
                  prompt: P
                  options: [only]
                  answer: only
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("fewer than two options"));
    }

    [Fact]
    public void QuizItemWithDuplicateOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            frequency: annual
            config:
              pass_mark: 50
              quiz:
                - id: q1
                  prompt: P
                  options: [a, a]
                  answer: a
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("duplicate option 'a'"));
    }

    [Fact]
    public void QuizAnswerNotInOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            frequency: annual
            config:
              pass_mark: 50
              quiz:
                - id: q1
                  prompt: P
                  options: [a, b]
                  answer: c
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("answer 'c'") && d.Message.Contains("not one of its options"));
    }

    // A config authored with the wrong YAML shape must be a diagnostic, never an exception: the typed
    // bind fails, no collector loads from that document, and the rest of the directory still loads. Each
    // list is authored on a type whose schema REGISTERS it, so the diagnostic under test can only be the
    // bind failure - on a type that does not register the key, an unknown-key diagnostic would satisfy a
    // bare non-empty assertion before the bind is ever reached. Both a scalar and a mapping are covered,
    // since either is a non-list where a list is required.
    [Theory]
    [InlineData("manual", "config: not-a-mapping")]
    [InlineData("manual", "config:\n  - one\n  - two")]
    [InlineData("manual", "config:\n  fields: not-a-list")]
    [InlineData("manual", "config:\n  fields:\n    id: f1")]
    [InlineData("training", "config:\n  pass_mark: 80\n  quiz: not-a-list")]
    [InlineData("training", "config:\n  pass_mark: 80\n  quiz:\n    id: q1")]
    [InlineData("integration", "config:\n  checks: not-a-list")]
    [InlineData("integration", "config:\n  checks:\n    source_key: \"12\"")]
    [InlineData("manual", "config:\n  fields:\n    - id: f1\n      label: L\n      type: single-choice\n      options: not-a-list")]
    public void WrongShapeConfigIsDiagnosticNotCrash(string type, string config)
    {
        var integrationFields = type == "integration" ? "provider: fleet\nconnection: fleet-prod\n" : string.Empty;
        using var dir = TempConfig.Create(("all.yaml", ValidSet($"""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: {type}
            frequency: annual
            {integrationFields}{config}
            """)));

        var result = ConfigLoader.Load(dir.Path);

        // The bind failure is the diagnostic, not an unknown-key cascade.
        Assert.Contains(result.Diagnostics, d => d.Message.StartsWith("Malformed YAML", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("on Collector config."));
        Assert.Empty(result.Config.Collectors);
        // Only the collector document failed; the rest of the file still loaded.
        Assert.Single(result.Config.Standards);
    }

    [Fact]
    public void ConfigThatIsNotAMappingEmitsNoPerKeyCascade()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: daily
            config: not-a-mapping
            """)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("Collector config"));
    }

    // An explicit-null `config:` is the one wrong-shape-adjacent case that is NOT a diagnostic: it binds
    // cleanly, so nothing the loader catches would report it, and the loader's own normalization is what
    // stops it dereferencing null later. Its verdict is the pre-merge one for an explicit-null config.
    [Fact]
    public void ExplicitNullConfigLoadsAsAnEmptyConfigWithNoDiagnostic()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: daily
            config:
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var collector = Assert.Single(result.Config.Collectors);
        Assert.NotNull(collector.Config);
        Assert.Empty(collector.Config.Fields);
        Assert.Empty(collector.Config.Quiz);
        Assert.Empty(collector.Config.Checks);
        Assert.Equal(string.Empty, collector.Config.Body);
        Assert.Equal(string.Empty, collector.Config.PassMark);
    }

    [Fact]
    public void ExplicitNullNestedCollectionsNormalizeToEmpty()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            config:
              fields:
              quiz:
            """)));

        var result = ConfigLoader.Load(dir.Path);

        var collector = Assert.Single(result.Config.Collectors);
        Assert.NotNull(collector.Config.Fields);
        Assert.Empty(collector.Config.Fields);
        Assert.NotNull(collector.Config.Quiz);
        Assert.Empty(collector.Config.Quiz);
    }

    [Fact]
    public void ExplicitNullFieldOptionsNormalizeToEmpty()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            config:
              fields:
                - id: f1
                  label: L
                  type: boolean
                  options:
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var field = Assert.Single(Assert.Single(result.Config.Collectors).Config.Fields);
        Assert.NotNull(field.Options);
        Assert.Empty(field.Options);
    }

    [Fact]
    public void ExplicitNullChecksItemIsKeptAndReported()
    {
        // The kept half of the null-sequence-item rule. A blank checks item deserializes to a null
        // element and is kept as an empty Check, so the validator reports its missing fields rather than
        // the loader silently dropping a malformed check.
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            connection: fleet-prod
            config:
              checks:
                - source_key: "12"
                  name: mfa-enforced
                  severity: Hard
                -
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.Equal(2, Assert.Single(result.Config.Collectors).Config.Checks.Count);
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("check source_key"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("check name"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("check severity"));
    }

    [Fact]
    public void ExplicitNullFieldItemIsDroppedAndTheCollectorStillValidates()
    {
        // The dropped half of the same rule, and the asymmetry is deliberate: dropping a null check would
        // swallow the malformed-check diagnostic above, while KEEPING a null form item would fail a
        // document that validates today. So the still-validates half is what this pins - `fields` is
        // registered for `manual`, so the only verdict on offer here is the drop's.
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            frequency: annual
            config:
              fields:
                -
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(Assert.Single(result.Config.Collectors).Config.Fields);
    }

    [Fact]
    public void ExplicitNullQuizItemIsDroppedAndTheCollectorStillValidates()
    {
        // The same half on the quiz list, authored where `quiz` is registered and required, so the
        // surviving item keeps the collector valid and the dropped one leaves no trace.
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            frequency: annual
            config:
              pass_mark: 80
              quiz:
                - id: q1
                  prompt: P
                  options: [a, b]
                  answer: a
                -
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var item = Assert.Single(Assert.Single(result.Config.Collectors).Config.Quiz);
        Assert.Equal("q1", item.Id);
    }

    [Fact]
    public void ConfigWithNoCollectorsStillLoadsAndValidates()
    {
        using var dir = TempConfig.Create(("all.yaml", $"{ValidStandard}\n---\n{ValidRequirement}"));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Config.Collectors);
    }
}
