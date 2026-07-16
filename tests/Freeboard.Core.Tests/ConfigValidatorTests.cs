using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void MissingRequiredFieldFails()
    {
        using var dir = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: A
                """),
            ("ctrl.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("ctrl-a") && d.Message.Contains("maps_to"));
    }

    [Fact]
    public void UnknownApiVersionFails()
    {
        using var dir = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.dev/v2
                kind: Standard
                id: std-a
                title: A
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown apiVersion") && d.Message.Contains("freeboard.dev/v2"));
    }

    [Fact]
    public void DuplicateIdFails()
    {
        using var dir = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: A
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: A again
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate Standard id 'std-a'"));
    }

    [Fact]
    public void DanglingMapsToFails()
    {
        using var dir = TempConfig.Create(
            ("ctrl.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - std-missing
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown Requirement id 'std-missing'"));
    }

    [Fact]
    public void DanglingScopeOrganisationReferenceFails()
    {
        using var dir = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: A
                """),
            ("scope.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                organisation: org-missing
                standard: std-a
                disposition: In
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown Organisation id 'org-missing'"));
    }

    [Fact]
    public void DanglingScopeStandardReferenceFails()
    {
        using var dir = TempConfig.Create(
            ("org.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Asset
                id: org-a
                title: Org A
                type: Company
                source: declared
                """),
            ("scope.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                organisation: org-a
                standard: std-missing
                disposition: In
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown Standard id 'std-missing'"));
    }

    [Fact]
    public void UnknownAssetTypeFails()
    {
        using var dir = TempConfig.Create(
            ("org.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Asset
                id: org-a
                title: Org A
                type: Guild
                source: declared
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("org-a") && d.Message.Contains("unknown type 'Guild'"));
    }

    [Fact]
    public void UnknownDispositionFails()
    {
        using var dir = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: A
                """),
            ("org.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Asset
                id: org-a
                title: Org A
                type: Company
                source: declared
                """),
            ("scope.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                organisation: org-a
                standard: std-a
                disposition: Maybe
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("unknown disposition 'Maybe'"));
    }

    [Fact]
    public void CompanyWithDepartmentChildLoadsAsTree()
    {
        using var dir = TempConfig.Create(
            ("org.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Asset
                id: ologist-products
                title: Ologist Products Ltd
                type: Company
                source: declared
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Asset
                id: ologist-products-eng
                title: Engineering
                type: Department
                source: declared
                parent: ologist-products
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var child = result.Config.Assets.Single(o => o.Id == "ologist-products-eng");
        Assert.Equal("ologist-products", child.Parent);
    }

    [Fact]
    public void DuplicateScopeMappingFails()
    {
        using var dir = TempConfig.Create(
            ("base.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: A
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Asset
                id: org-a
                title: Org A
                type: Company
                source: declared
                """),
            ("scopes.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                organisation: org-a
                standard: std-a
                disposition: In
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-b
                title: Scope B
                organisation: org-a
                standard: std-a
                disposition: Out
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("org-a") && d.Message.Contains("std-a") && d.Message.Contains("more than once"));
    }

    [Fact]
    public void DuplicateMapsToIdFails()
    {
        using var dir = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: iso-27001
                title: ISO 27001
                """),
            ("ctrl.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - iso-27001
                  - iso-27001
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("ctrl-a") && d.Message.Contains("maps_to")
                && d.Message.Contains("duplicate") && d.Message.Contains("iso-27001"));
    }

    [Fact]
    public void OmittedScopeReferencesFail()
    {
        using var dir = TempConfig.Create(
            ("scope.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("organisation"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("standard"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("disposition"));
    }

    [Fact]
    public void ExplicitNullMapsToYieldsDiagnosticWithoutThrowing()
    {
        using var dir = TempConfig.Create(
            ("ctrl.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("ctrl-a") && d.Message.Contains("maps_to"));
    }

    [Fact]
    public void AllErrorsReportedNotJustFirst()
    {
        using var dir = TempConfig.Create(
            ("bad.yaml", """
                apiVersion: freeboard.dev/v2
                kind: Standard
                id: std-a
                title: A
                ---
                apiVersion: freeboard.dev/v2
                kind: Standard
                id: std-b
                title: B
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.Equal(2, result.Diagnostics.Count(d => d.Message.Contains("unknown apiVersion")));
    }

    [Fact]
    public void TitleChangeDoesNotChangeIdentity()
    {
        using var withTitleOne = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: Original
                version: "1.0"
                authority: Example Authority
                """),
            ("req.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Requirement
                id: req-a
                title: Requirement A
                standard: std-a
                theme: Theme A
                statement: Do the thing.
                citation_label: Source A
                citation_url: https://example.com/a
                """),
            ("ctrl.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - req-a
                """));

        using var withTitleTwo = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: Renamed
                version: "1.0"
                authority: Example Authority
                """),
            ("req.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Requirement
                id: req-a
                title: Requirement A
                standard: std-a
                theme: Theme A
                statement: Do the thing.
                citation_label: Source A
                citation_url: https://example.com/a
                """),
            ("ctrl.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - req-a
                """));

        var resultOne = ConfigValidator.LoadAndValidate(withTitleOne.Path);
        var resultTwo = ConfigValidator.LoadAndValidate(withTitleTwo.Path);

        // Both resolve: the Control's maps_to matches a Requirement id regardless of the Standard title.
        Assert.True(resultOne.IsValid);
        Assert.True(resultTwo.IsValid);
        Assert.Equal("std-a", resultTwo.Config.Standards[0].Id);
        Assert.NotEqual(resultOne.Config.Standards[0].Title, resultTwo.Config.Standards[0].Title);
    }
}
