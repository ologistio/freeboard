using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the (type, provider) config schema registry as the single owner of a collector's config key
/// set: an unregistered key rejected per pair whatever its value, a registered required key satisfied
/// only by a non-empty value, the empty schemas accepting nothing, no cascade when the type or provider
/// resolves no schema, nested item keys left unchecked, and registry completeness over every pair the
/// token sets can produce.
/// </summary>
public sealed class CollectorConfigSchemaTests
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

    private const string ValidConnection = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Integration
        id: fleet-prod
        title: Fleet Production
        provider: fleet
        base_url: https://fleet.example.com
        discovery_cadence: daily
        """;

    private static string ValidSet(string collector) =>
        $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{ControlWithEvaluation}\n---\n{ValidConnection}\n---\n{collector}";

    /// <summary>A collector of <paramref name="type"/> carrying <paramref name="config"/> verbatim.</summary>
    private static string Collector(string type, string config)
    {
        var integrationFields = type == "integration" ? "provider: fleet\nconnection: fleet-prod\n" : string.Empty;
        return $"""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: {type}
            frequency: daily
            {integrationFields}{config}
            """;
    }

    // Every pair rejects a key its schema does not name.
    [Theory]
    [InlineData("manual", "config:\n  pass_mark: 80", "pass_mark")]
    [InlineData("manual", "config:\n  checks:\n    - source_key: \"12\"\n      name: n\n      severity: Hard", "checks")]
    [InlineData("training", "config:\n  checks:\n    - source_key: \"12\"\n      name: n\n      severity: Hard", "checks")]
    [InlineData("script", "config:\n  checks:\n    - source_key: \"12\"\n      name: n\n      severity: Hard", "checks")]
    [InlineData("agent", "config:\n  checks:\n    - source_key: \"12\"\n      name: n\n      severity: Hard", "checks")]
    [InlineData("script", "config:\n  body: anything", "body")]
    [InlineData("agent", "config:\n  fields:\n    - id: f1\n      label: L\n      type: boolean", "fields")]
    [InlineData("integration", "config:\n  body: anything\n  checks:\n    - source_key: \"12\"\n      name: n\n      severity: Hard", "body")]
    public void UnregisteredConfigKeyIsRejected(string type, string config, string key)
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet(Collector(type, config))));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains($"Unknown field '{key}' on Collector config."));
    }

    // Requiredness is evaluated on the parsed VALUE: an empty list, a blank scalar, and an authored null
    // all count as absent, exactly as the pre-merge value rules made them.
    [Theory]
    [InlineData("training", "config:\n  quiz:\n    - id: q1\n      prompt: P\n      options: [a, b]\n      answer: a", "pass_mark")]
    [InlineData("training", "config:\n  pass_mark: 80", "quiz")]
    [InlineData("training", "config:\n  pass_mark: 80\n  quiz: []", "quiz")]
    [InlineData("training", "config:\n  pass_mark: \"\"\n  quiz:\n    - id: q1\n      prompt: P\n      options: [a, b]\n      answer: a", "pass_mark")]
    [InlineData("training", "config:\n  pass_mark:\n  quiz:\n    - id: q1\n      prompt: P\n      options: [a, b]\n      answer: a", "pass_mark")]
    [InlineData("integration", "config:\n  checks: []", "checks")]
    [InlineData("integration", "config:", "checks")]
    [InlineData("integration", "", "checks")]
    public void MissingOrEmptyRequiredConfigKeyIsRejected(string type, string config, string key)
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet(Collector(type, config))));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d =>
            d.Message.Contains("collector-a") && d.Message.Contains($"missing required config key '{key}'"));
    }

    [Fact]
    public void BlankRequiredScalarIsAMissingKeyNotARangeError()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet(Collector("training", """
            config:
              pass_mark: ""
              quiz:
                - id: q1
                  prompt: P
                  options: [a, b]
                  answer: a
            """))));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("missing required config key 'pass_mark'"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("invalid pass_mark"));
    }

    // Both halves of the empty-value rule, which are deliberately evaluated against different things.
    [Fact]
    public void EmptyListIsAbsentForARequiredKeyAndStillPresentForAnUnregisteredOne()
    {
        using var trainingDir = TempConfig.Create(("all.yaml", ValidSet(Collector("training", "config:\n  pass_mark: 80\n  quiz: []"))));
        using var manualDir = TempConfig.Create(("all.yaml", ValidSet(Collector("manual", "config:\n  quiz: []"))));

        var training = ConfigValidator.LoadAndValidate(trainingDir.Path);
        var manual = ConfigValidator.LoadAndValidate(manualDir.Path);

        Assert.Contains(training.Diagnostics, d => d.Message.Contains("missing required config key 'quiz'"));
        Assert.Contains(manual.Diagnostics, d => d.Message.Contains("Unknown field 'quiz' on Collector config."));
    }

    [Theory]
    [InlineData("script")]
    [InlineData("agent")]
    [InlineData("manual")]
    public void OmittedConfigIsValidWhereTheSchemaRequiresNothing(string type)
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet(Collector(type, string.Empty))));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void ManualConfigCarryingNeitherBodyNorFieldsIsValid()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet(Collector("manual", "config: {}"))));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    // No schema resolves for a bad token, so the token diagnostic is the only one the author gets.
    [Fact]
    public void UnknownTypeDoesNotCascadeConfigDiagnostics()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet(Collector("webhook", "config:\n  body: b\n  nonsense: n"))));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown type 'webhook'"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("on Collector config."));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("missing required config key"));
    }

    [Fact]
    public void UnknownProviderDoesNotCascadeConfigDiagnostics()
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
              nonsense: n
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown provider 'intune'"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("on Collector config."));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("missing required config key"));
    }

    // An ABSENT provider resolves no schema for the same reason an unknown one does not: which keys the
    // pair accepts and requires is a property of the PAIR, so with no provider there is no requiredness
    // to report that would not be one provider's key set hard-coded. The author gets the missing-provider
    // diagnostic, fixes it, and the required-key check then runs against the resolved pair.
    [Fact]
    public void AbsentProviderDoesNotCascadeConfigDiagnostics()
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
              nonsense: n
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("missing required field 'provider'"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("on Collector config."));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("missing required config key"));
    }

    // The mirror of the absent-provider case, and it is decided the other way on the same principle. A
    // provider a non-integration type cannot legally carry selects no key set - every row such a type
    // registers is a no-provider row - so it must not conceal one either. Suppressing here would cost the
    // author every config diagnostic on the collector for a token whose only repair is deletion.
    [Fact]
    public void StrayProviderOnANonIntegrationTypeStillResolvesItsSchema()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: training
            provider: fleet
            frequency: daily
            config:
              quiz:
                - id: q1
                  prompt: P
                  options: [a, b]
                  answer: a
              nonsense: n
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("declares 'provider'"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("missing required config key 'pass_mark'"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'nonsense' on Collector config."));
    }

    // Unknown-key rejection applies to the config map's own keys only, matching the pre-merge carve-out:
    // an extra key inside a nested item is ignored while the item's required keys are still enforced.
    [Fact]
    public void UnknownKeyInsideANestedItemIsIgnored()
    {
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
                  nonsense: ignored
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        // Ignored, not merely tolerated: the item's own keys still bind to their members.
        var check = Assert.Single(Assert.Single(result.Config.Collectors).Config.Checks);
        Assert.Equal(("12", "mfa-enforced", "Hard"), (check.SourceKey, check.Name, check.Severity));
    }

    [Fact]
    public void UnknownKeyInsideANestedFieldOrQuizItemIsIgnored()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet(Collector("training", """
            config:
              fields:
                - id: f1
                  label: L
                  type: boolean
                  nonsense: ignored
              pass_mark: 80
              quiz:
                - id: q1
                  prompt: P
                  options: [a, b]
                  answer: a
                  nonsense: ignored
            """))));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    // The registry must be COMPLETE over every pair the shared token sets can produce. An unregistered
    // but VALID pair resolves no schema, so no unknown-key and no required-key diagnostic fires and
    // `config` is silently free-form for it - the exact hole the registry closes. Enumerating the token
    // sets rather than listing the pairs by hand is what makes adding a provider without a registry row
    // fail here.
    [Fact]
    public void EveryReachableTypeAndProviderPairResolvesASchema()
    {
        foreach (var provider in IntegrationProvider.Tokens)
        {
            Assert.NotNull(CollectorConfigSchema.For("integration", provider));
        }

        foreach (var type in ConfigValidator.CollectorTypeTokens.Where(t => t != "integration"))
        {
            Assert.NotNull(CollectorConfigSchema.For(type, null));
        }
    }

    [Fact]
    public void NoSchemaResolvesForAnUnknownTypeOrProvider()
    {
        Assert.Null(CollectorConfigSchema.For("webhook", null));
        Assert.Null(CollectorConfigSchema.For("webhook", "fleet"));
        Assert.Null(CollectorConfigSchema.For("integration", "intune"));
        // A provider-bearing pair is keyed on both components: integration without one resolves nothing.
        // The type-alone retry does not rescue it, because no (integration, -) row is registered.
        Assert.Null(CollectorConfigSchema.For("integration", null));
    }

    [Fact]
    public void NonIntegrationTypeResolvesTheSameSchemaWithOrWithoutAProvider()
    {
        foreach (var type in ConfigValidator.CollectorTypeTokens.Where(t => t != "integration"))
        {
            Assert.Same(CollectorConfigSchema.For(type, null), CollectorConfigSchema.For(type, "fleet"));
        }
    }
}
