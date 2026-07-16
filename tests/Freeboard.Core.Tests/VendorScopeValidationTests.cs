using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the Vendor and VendorScope kinds: distinct kind routing, required fields, the
/// exactly-one-target rule (requirement or control, never both or neither), resolvable vendor and
/// target references, the disposition enum, the unknown-field rejection, duplicate ids, the unique
/// (vendor, target) pairs, and the net-new justification-required-when-Out rule. The loader and
/// validator never throw or print.
/// </summary>
public sealed class VendorScopeValidationTests
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

    private static string ValidSet(string vendorScope) =>
        $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{ValidControl}\n---\n{ValidOwnerCompany}\n---\n{ValidVendor}\n---\n{vendorScope}";

    [Fact]
    public void VendorLoadsWithIdAndTitle()
    {
        using var dir = TempConfig.Create(("v.yaml", ValidVendor));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        var vendor = Assert.Single(result.Config.Assets);
        Assert.Equal("vendor-a", vendor.Id);
        Assert.Equal("Vendor A", vendor.Title);
        Assert.Equal("Vendor", vendor.Type);
    }

    [Fact]
    public void VendorScopeTargetingRequirementLoadsAndRoutesDistinctly()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: Except req-a for vendor-a
            vendor: vendor-a
            requirement: req-a
            disposition: Out
            justification: Accounting package supports MFA but not SSO.
            """)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        var vendorScope = Assert.Single(result.Config.VendorScopes);
        Assert.Equal("vs-a", vendorScope.Id);
        Assert.Equal("vendor-a", vendorScope.Vendor);
        Assert.Equal("req-a", vendorScope.Requirement);
        Assert.Equal(string.Empty, vendorScope.Control);
        Assert.Equal("Out", vendorScope.Disposition);
        Assert.Equal("Accounting package supports MFA but not SSO.", vendorScope.Justification);
    }

    [Fact]
    public void VendorScopeTargetingControlLoads()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: Except ctrl-a for vendor-a
            vendor: vendor-a
            control: ctrl-a
            disposition: Out
            justification: No logins - N/A.
            """)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        var vendorScope = Assert.Single(result.Config.VendorScopes);
        Assert.Equal("ctrl-a", vendorScope.Control);
        Assert.Equal(string.Empty, vendorScope.Requirement);
    }

    [Fact]
    public void ValidVendorScopePassesValidation()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: Except req-a for vendor-a
            vendor: vendor-a
            requirement: req-a
            disposition: Out
            justification: Supports MFA but not SSO.
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void UnknownFieldIsRejected()
    {
        using var dir = TempConfig.Create(("vs.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            requirement: req-a
            disposition: In
            organisation: org-a
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'organisation'") && d.Message.Contains("VendorScope"));
    }

    [Fact]
    public void MissingVendorFieldFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            requirement: req-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vs-a") && d.Message.Contains("vendor"));
    }

    [Fact]
    public void BothTargetsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            requirement: req-a
            control: ctrl-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vs-a") && d.Message.Contains("exactly one"));
    }

    [Fact]
    public void NeitherTargetFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vs-a") && d.Message.Contains("exactly one"));
    }

    [Fact]
    public void UnknownVendorFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-missing
            requirement: req-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vs-a") && d.Message.Contains("unknown Vendor id 'vendor-missing'"));
    }

    [Fact]
    public void UnknownRequirementTargetFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            requirement: req-missing
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vs-a") && d.Message.Contains("unknown Requirement id 'req-missing'"));
    }

    [Fact]
    public void UnknownControlTargetFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            control: ctrl-missing
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vs-a") && d.Message.Contains("unknown Control id 'ctrl-missing'"));
    }

    [Fact]
    public void BadDispositionFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            requirement: req-a
            disposition: Maybe
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vs-a") && d.Message.Contains("unknown disposition 'Maybe'"));
    }

    [Fact]
    public void WrongApiVersionFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v2
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            requirement: req-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vs-a") && d.Message.Contains("unknown apiVersion"));
    }

    [Fact]
    public void DuplicateVendorScopeIdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            {ValidRequirement}
            ---
            {ValidControl}
            ---
            {ValidOwnerCompany}
            ---
            {ValidVendor}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            requirement: req-a
            disposition: In
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            control: ctrl-a
            disposition: In
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate VendorScope id 'vs-a'"));
    }

    [Fact]
    public void DuplicateVendorRequirementPairFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            {ValidRequirement}
            ---
            {ValidControl}
            ---
            {ValidOwnerCompany}
            ---
            {ValidVendor}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            requirement: req-a
            disposition: In
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-b
            title: T
            vendor: vendor-a
            requirement: req-a
            disposition: Out
            justification: Reconsidered.
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("vendor 'vendor-a'") && d.Message.Contains("requirement 'req-a'") && d.Message.Contains("more than once"));
    }

    [Fact]
    public void DuplicateVendorControlPairFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            {ValidRequirement}
            ---
            {ValidControl}
            ---
            {ValidOwnerCompany}
            ---
            {ValidVendor}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            control: ctrl-a
            disposition: In
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-b
            title: T
            vendor: vendor-a
            control: ctrl-a
            disposition: Out
            justification: Reconsidered.
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("vendor 'vendor-a'") && d.Message.Contains("control 'ctrl-a'") && d.Message.Contains("more than once"));
    }

    [Fact]
    public void OutWithoutJustificationFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            requirement: req-a
            disposition: Out
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vs-a") && d.Message.Contains("justification"));
    }

    [Fact]
    public void OutWithWhitespaceJustificationFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            requirement: req-a
            disposition: Out
            justification: "   "
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vs-a") && d.Message.Contains("justification"));
    }

    [Fact]
    public void OutWithJustificationPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            requirement: req-a
            disposition: Out
            justification: Supports MFA but not SSO.
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void InWithoutJustificationPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: VendorScope
            id: vs-a
            title: T
            vendor: vendor-a
            requirement: req-a
            disposition: In
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void DuplicateVendorIdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidOwnerCompany}
            ---
            {ValidVendor}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A duplicate
            type: Vendor
            source: declared
            owner: org-a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate Asset id 'vendor-a'"));
    }

    [Fact]
    public void ConfigWithNoVendorsStillLoadsAndValidates()
    {
        using var dir = TempConfig.Create(("all.yaml", $"{ValidStandard}\n---\n{ValidRequirement}"));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Config.Assets);
        Assert.Empty(result.Config.VendorScopes);
    }
}
