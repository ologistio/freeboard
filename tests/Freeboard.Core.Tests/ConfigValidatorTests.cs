using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void MissingRequiredFieldFails()
    {
        using var dir = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Standard
                id: std-a
                title: A
                """),
            ("ctrl.yaml", """
                apiVersion: freeboard.io/v1alpha1
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
                apiVersion: freeboard.io/v2
                kind: Standard
                id: std-a
                title: A
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown apiVersion") && d.Message.Contains("freeboard.io/v2"));
    }

    [Fact]
    public void DuplicateIdFails()
    {
        using var dir = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Standard
                id: std-a
                title: A
                ---
                apiVersion: freeboard.io/v1alpha1
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
                apiVersion: freeboard.io/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - std-missing
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown Standard id 'std-missing'"));
    }

    [Fact]
    public void DanglingControlsReferenceFails()
    {
        using var dir = TempConfig.Create(
            ("scope.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                controls:
                  - ctrl-missing
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown Control id 'ctrl-missing'"));
    }

    [Fact]
    public void DuplicateMapsToIdFails()
    {
        using var dir = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Standard
                id: iso-27001
                title: ISO 27001
                """),
            ("ctrl.yaml", """
                apiVersion: freeboard.io/v1alpha1
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
    public void DuplicateScopeControlIdFails()
    {
        using var dir = TempConfig.Create(
            ("ctrl.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - std-a
                """),
            ("std.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Standard
                id: std-a
                title: A
                """),
            ("scope.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                controls:
                  - ctrl-a
                  - ctrl-a
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("scope-a") && d.Message.Contains("controls")
                && d.Message.Contains("duplicate") && d.Message.Contains("ctrl-a"));
    }

    [Fact]
    public void OmittedScopeControlsFails()
    {
        using var dir = TempConfig.Create(
            ("scope.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("controls"));
    }

    [Fact]
    public void EmptyScopeControlsFails()
    {
        using var dir = TempConfig.Create(
            ("scope.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                controls: []
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("controls"));
    }

    [Fact]
    public void ExplicitNullMapsToYieldsDiagnosticWithoutThrowing()
    {
        using var dir = TempConfig.Create(
            ("ctrl.yaml", """
                apiVersion: freeboard.io/v1alpha1
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
    public void ExplicitNullControlsYieldsDiagnosticWithoutThrowing()
    {
        using var dir = TempConfig.Create(
            ("scope.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                controls:
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope-a") && d.Message.Contains("controls"));
    }

    [Fact]
    public void AllErrorsReportedNotJustFirst()
    {
        using var dir = TempConfig.Create(
            ("bad.yaml", """
                apiVersion: freeboard.io/v2
                kind: Standard
                id: std-a
                title: A
                ---
                apiVersion: freeboard.io/v2
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
                apiVersion: freeboard.io/v1alpha1
                kind: Standard
                id: std-a
                title: Original
                """),
            ("ctrl.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - std-a
                """));

        using var withTitleTwo = TempConfig.Create(
            ("std.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Standard
                id: std-a
                title: Renamed
                """),
            ("ctrl.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - std-a
                """));

        var resultOne = ConfigValidator.LoadAndValidate(withTitleOne.Path);
        var resultTwo = ConfigValidator.LoadAndValidate(withTitleTwo.Path);

        // Both resolve: the Control's maps_to matches the Standard's id regardless of title.
        Assert.True(resultOne.IsValid);
        Assert.True(resultTwo.IsValid);
        Assert.Equal("std-a", resultTwo.Config.Standards[0].Id);
        Assert.NotEqual(resultOne.Config.Standards[0].Title, resultTwo.Config.Standards[0].Title);
    }
}
