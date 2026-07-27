using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the unified Scope kind: loader routing and field binding, required fields, the
/// exactly-one-target rule (standard/requirement/control - never none, two, or three), resolvable
/// target references (a dangling target is an Error), a dangling-tolerant scalar subject (a subject
/// naming no asset is a non-blocking Warning that does not fail validation), the
/// Vendor-subject-cannot-target-a-standard rule, the disposition enum, the generalized
/// Out-requires-justification rule (and In omitting it), duplicate ids, the three unique
/// (subject, target) pairs, unknown-field rejection, and the retired RequirementScope/VendorScope
/// kinds now routing to an unknown-kind diagnostic. The loader and validator never throw or print.
/// </summary>
public sealed class ScopeValidationTests
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

    private const string ValidControl = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Control
        id: ctrl-a
        title: Control A
        maps_to:
          - req-a
        """;

    private const string ValidCompany = """
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

    // Every referenceable target and both subject asset types, so a scope under test resolves.
    private static string Base() =>
        $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{ValidControl}\n---\n{ValidCompany}\n---\n{ValidVendor}";

    private static string ValidSet(string scope) => $"{Base()}\n---\n{scope}";

    [Fact]
    public void ScopeLoadsAndBindsAllFields()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            disposition: In
            """)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        var scope = Assert.Single(result.Config.Scopes);
        Assert.Equal("scope-a", scope.Id);
        Assert.Equal("org-a", scope.Subject);
        Assert.Equal("std-a", scope.Standard);
        Assert.Equal(string.Empty, scope.Requirement);
        Assert.Equal(string.Empty, scope.Control);
        Assert.Equal("In", scope.Disposition);
    }

    [Fact]
    public void OrgSubjectTargetingStandardPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void OrgSubjectTargetingRequirementPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            requirement: req-a
            disposition: Out
            justification: Handled by a compensating control.
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void OrgSubjectTargetingControlPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            control: ctrl-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void VendorSubjectTargetingRequirementPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: vendor-a
            requirement: req-a
            disposition: Out
            justification: Supports MFA but not SSO.
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void VendorSubjectTargetingControlPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: vendor-a
            control: ctrl-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void NoTargetFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("exactly one"));
    }

    [Fact]
    public void TwoTargetsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            requirement: req-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("exactly one"));
    }

    [Fact]
    public void ThreeTargetsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            requirement: req-a
            control: ctrl-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("exactly one"));
    }

    [Fact]
    public void MissingRequiredFieldsFail()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            standard: std-a
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("title"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("subject"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("disposition"));
    }

    [Fact]
    public void OutWithoutJustificationFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            disposition: Out
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("justification"));
    }

    [Fact]
    public void OutWithWhitespaceJustificationFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            disposition: Out
            justification: "   "
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("justification"));
    }

    [Fact]
    public void OutWithJustificationPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            disposition: Out
            justification: Documented exception.
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void InWithoutJustificationPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void BadDispositionFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            disposition: Maybe
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("unknown disposition 'Maybe'"));
    }

    [Fact]
    public void DanglingStandardTargetFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-missing
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("unknown Standard id 'std-missing'"));
    }

    [Fact]
    public void DanglingRequirementTargetFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            requirement: req-missing
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("unknown Requirement id 'req-missing'"));
    }

    [Fact]
    public void DanglingControlTargetFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            control: ctrl-missing
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("unknown Control id 'ctrl-missing'"));
    }

    [Fact]
    public void DanglingSubjectIsNonBlockingWarning()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: asset-missing
            standard: std-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        // A subject naming no asset warns but does not fail validation.
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var warning = Assert.Single(
            result.Diagnostics,
            d => d.Code == DiagnosticCode.ScopeSubjectUnresolved);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("scope-a", warning.Message);
        Assert.Contains("asset-missing", warning.Message);
        Assert.Contains("resolves to no asset", warning.Message);
    }

    [Fact]
    public void VendorSubjectTargetingStandardFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: vendor-a
            standard: std-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("scope-a") && d.Message.Contains("vendor-a")
                && d.Message.Contains("is a Vendor and cannot target a standard"));
    }

    [Fact]
    public void DuplicateIdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {Base()}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            disposition: In
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A again
            subject: org-a
            requirement: req-a
            disposition: In
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate Scope id 'scope-a'"));
    }

    [Fact]
    public void DuplicateSubjectStandardPairFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {Base()}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            disposition: In
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-b
            title: Scope B
            subject: org-a
            standard: std-a
            disposition: Out
            justification: Reconsidered.
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("subject 'org-a'") && d.Message.Contains("standard 'std-a'") && d.Message.Contains("more than once"));
    }

    [Fact]
    public void DuplicateSubjectRequirementPairFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {Base()}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            requirement: req-a
            disposition: In
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-b
            title: Scope B
            subject: org-a
            requirement: req-a
            disposition: Out
            justification: Reconsidered.
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("subject 'org-a'") && d.Message.Contains("requirement 'req-a'") && d.Message.Contains("more than once"));
    }

    [Fact]
    public void DuplicateSubjectControlPairFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {Base()}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            control: ctrl-a
            disposition: In
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-b
            title: Scope B
            subject: org-a
            control: ctrl-a
            disposition: Out
            justification: Reconsidered.
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("subject 'org-a'") && d.Message.Contains("control 'ctrl-a'") && d.Message.Contains("more than once"));
    }

    [Fact]
    public void UnknownFieldIsRejected()
    {
        // 'organisation' was a field on the old Scope/RequirementScope kinds; the unified Scope
        // carries only 'subject', so it is now an unknown field.
        using var dir = TempConfig.Create(("scope.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            disposition: In
            organisation: org-a
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'organisation'") && d.Message.Contains("Scope"));
    }

    [Fact]
    public void WrongApiVersionFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v2
            kind: Scope
            id: scope-a
            title: Scope A
            subject: org-a
            standard: std-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("unknown apiVersion"));
    }

    [Fact]
    public void RetiredRequirementScopeKindIsUnknownKind()
    {
        using var dir = TempConfig.Create(("rs.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: RequirementScope
            id: rs-a
            title: Exclude req-a
            organisation: org-a
            requirement: req-a
            disposition: Out
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown kind 'RequirementScope'"));
        Assert.Empty(result.Config.Scopes);
    }

    [Fact]
    public void RetiredVendorScopeKindIsUnknownKind()
    {
        using var dir = TempConfig.Create(("vs.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: Except req-a for vendor-a
            vendor: vendor-a
            requirement: req-a
            disposition: Out
            justification: Supports MFA but not SSO.
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown kind 'VendorScope'"));
        Assert.Empty(result.Config.Scopes);
    }

    [Fact]
    public void ConfigWithNoScopesStillLoadsAndValidates()
    {
        using var dir = TempConfig.Create(("all.yaml", Base()));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Config.Scopes);
    }
}
