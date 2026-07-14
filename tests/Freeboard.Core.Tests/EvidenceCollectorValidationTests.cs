using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the EvidenceCollector kind and the Control evaluation rule: distinct kind routing, required
/// fields, resolvable control/vendor references, the type and frequency token sets, the optional
/// threshold range check (a malformed value is a diagnostic, not a crash), duplicate ids, unknown
/// fields, the evaluation enum, the evaluation-required-when-collectors rule, and the config-map
/// normalization. The loader and validator never throw or print.
/// </summary>
public sealed class EvidenceCollectorValidationTests
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

    private const string ValidVendor = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Vendor
        id: vendor-a
        title: Vendor A
        """;

    private static string ValidSet(string collector, string control = ControlWithEvaluation) =>
        $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{control}\n---\n{ValidVendor}\n---\n{collector}";

    [Fact]
    public void EvidenceCollectorLoadsAttachedToControlWithOptionalFields()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: Endpoint MFA via Crowdstrike
            control: ctrl-a
            vendor: vendor-a
            type: integration
            frequency: daily
            threshold: 100
            config:
              endpoint: policies.mfa
            """)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        var collector = Assert.Single(result.Config.EvidenceCollectors);
        Assert.Equal("collector-a", collector.Id);
        Assert.Equal("ctrl-a", collector.Control);
        Assert.Equal("vendor-a", collector.Vendor);
        Assert.Equal("integration", collector.Type);
        Assert.Equal("daily", collector.Frequency);
        Assert.Equal("100", collector.Threshold);
        Assert.Equal("policies.mfa", collector.Config["endpoint"]);
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
    public void ValidEvidenceCollectorPassesValidation()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: manual-attestation
            frequency: annual
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void OptionalFieldsOmittedStillValid()
    {
        // No vendor, no threshold, no config: all optional.
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: weekly
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var collector = Assert.Single(result.Config.EvidenceCollectors);
        Assert.Equal(string.Empty, collector.Vendor);
        Assert.Equal(string.Empty, collector.Threshold);
        Assert.Empty(collector.Config);
    }

    [Fact]
    public void UnknownFieldIsRejected()
    {
        using var dir = TempConfig.Create(("ec.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            frequency: daily
            organisation: org-a
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'organisation'") && d.Message.Contains("EvidenceCollector"));
    }

    [Fact]
    public void MissingControlFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            type: integration
            frequency: daily
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("control"));
    }

    [Fact]
    public void MissingTypeFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            frequency: daily
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("type"));
    }

    [Fact]
    public void MissingFrequencyFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("frequency"));
    }

    [Fact]
    public void UnknownTypeFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
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

    [Fact]
    public void UnknownFrequencyFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
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
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
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
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
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
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-missing
            type: integration
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
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            vendor: vendor-missing
            type: integration
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
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            frequency: daily
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: weekly
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate EvidenceCollector id 'collector-a'"));
    }

    [Fact]
    public void WrongApiVersionFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v2
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
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
        var controlNoEvaluation = """
            apiVersion: freeboard.dev/v1alpha1
            kind: Control
            id: ctrl-a
            title: Control A
            maps_to:
              - req-a
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            frequency: daily
            """, controlNoEvaluation)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("ctrl-a") && d.Message.Contains("missing required field 'evaluation'"));
    }

    [Fact]
    public void ControlWithCollectorAndEvaluationPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
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
        var controlNoEvaluation = """
            apiVersion: freeboard.dev/v1alpha1
            kind: Control
            id: ctrl-a
            title: Control A
            maps_to:
              - req-a
            """;
        using var dir = TempConfig.Create(("all.yaml", $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{controlNoEvaluation}"));

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
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-ghost
            type: integration
            frequency: daily
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown Control id 'ctrl-ghost'"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("ctrl-ghost") && d.Message.Contains("evaluation"));
    }

    [Fact]
    public void NonScalarConfigValueIsDiagnosticNotCrash()
    {
        // A config value that is a nested map cannot bind to string -> string. The loader must return a
        // diagnostic (never an uncaught exception), and other kinds still load.
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            frequency: daily
            config:
              nested:
                deep: value
            """)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.NotEmpty(result.Diagnostics);
        // The standard/requirement/control/vendor documents still loaded; only the collector failed.
        Assert.Single(result.Config.Standards);
        Assert.Empty(result.Config.EvidenceCollectors);
    }

    [Fact]
    public void ExplicitNullConfigNormalizesToEmptyMap()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: daily
            config:
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var collector = Assert.Single(result.Config.EvidenceCollectors);
        Assert.NotNull(collector.Config);
        Assert.Empty(collector.Config);
    }

    [Fact]
    public void ConfigWithNoCollectorsStillLoadsAndValidates()
    {
        using var dir = TempConfig.Create(("all.yaml", $"{ValidStandard}\n---\n{ValidRequirement}"));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Config.EvidenceCollectors);
    }
}
