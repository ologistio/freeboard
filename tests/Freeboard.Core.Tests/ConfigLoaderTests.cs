using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

public sealed class ConfigLoaderTests
{
    [Fact]
    public void ValidConfigLoadsWithCorrectCounts()
    {
        using var dir = TempConfig.Create(
            ("standards.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Standard
                id: std-a
                title: Standard A
                """),
            ("controls.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - std-a
                """),
            ("scopes.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                controls:
                  - ctrl-a
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Single(result.Config.Standards);
        Assert.Single(result.Config.Controls);
        Assert.Single(result.Config.Scopes);

        var standard = result.Config.Standards[0];
        Assert.Equal("std-a", standard.Id);
        Assert.Equal("Standard A", standard.Title);
        Assert.NotEqual(standard.Id, standard.Title);
        Assert.Equal(["std-a"], result.Config.Controls[0].MapsTo);
        Assert.Equal(["ctrl-a"], result.Config.Scopes[0].Controls);
    }

    [Fact]
    public void MultipleDocumentsInOneFileAllParse()
    {
        using var dir = TempConfig.Create(
            ("all.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Standard
                id: std-a
                title: A
                ---
                apiVersion: freeboard.io/v1alpha1
                kind: Standard
                id: std-b
                title: B
                ---
                apiVersion: freeboard.io/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - std-a
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Equal(2, result.Config.Standards.Count);
        Assert.Single(result.Config.Controls);
    }

    [Fact]
    public void MissingDirectoryReturnsDiagnosticNotException()
    {
        var result = ConfigLoader.Load("/no/such/dir/here");

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("not found"));
    }

    [Fact]
    public void MalformedYamlReturnsDiagnosticNotException()
    {
        using var dir = TempConfig.Create(
            ("bad.yaml", "kind: Standard\n  id: x\n :::not valid"));

        var result = ConfigLoader.Load(dir.Path);

        Assert.False(result.IsValid);
        var diag = Assert.Single(result.Diagnostics);
        Assert.Contains("Malformed YAML", diag.Message);
        Assert.Equal("bad.yaml", diag.File);
    }

    [Fact]
    public void MissingKindIsLoaderDiagnostic()
    {
        using var dir = TempConfig.Create(
            ("x.yaml", """
                apiVersion: freeboard.io/v1alpha1
                id: std-a
                title: A
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("no 'kind'"));
    }

    [Fact]
    public void UnknownKindIsLoaderDiagnostic()
    {
        using var dir = TempConfig.Create(
            ("x.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Widget
                id: w-a
                title: A
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown kind 'Widget'"));
        Assert.Empty(result.Config.Standards);
    }

    [Fact]
    public void UnknownFieldIsRejected()
    {
        using var dir = TempConfig.Create(
            ("x.yaml", """
                apiVersion: freeboard.io/v1alpha1
                kind: Standard
                id: std-a
                title: A
                colour: blue
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'colour'"));
    }

    [Fact]
    public void LoadOrderMatchesNormalizedPathThenInFileOrder()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "order");

        var result = ConfigLoader.Load(fixtureDir);

        Assert.Equal(["a1", "a2", "b1", "b2"], result.Config.Standards.Select(s => s.Id).ToArray());
    }
}
