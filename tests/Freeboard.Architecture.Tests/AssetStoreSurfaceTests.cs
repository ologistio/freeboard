using Freeboard.Persistence;

namespace Freeboard.Architecture.Tests;

/// <summary>
/// Pins the read/write split: the read store <see cref="IAssetStore"/> exposes lookups only, so no code
/// path mutates asset state through the read interface. State changes go through
/// <see cref="IAssetWriteStore"/> alone.
/// </summary>
public sealed class AssetStoreSurfaceTests
{
    private static readonly string[] MutatingVerbs =
        ["Create", "Update", "Delete", "Upsert", "Retire", "Insert", "Save", "Remove", "Write", "Set", "Append"];

    [Fact]
    public void ReadStoreExposesLookupsOnlyNoMutators()
    {
        var methods = typeof(IAssetStore).GetMethods();
        Assert.NotEmpty(methods);

        foreach (var method in methods)
        {
            Assert.StartsWith("Get", method.Name, StringComparison.Ordinal);
            Assert.DoesNotContain(
                MutatingVerbs,
                verb => method.Name.Contains(verb, StringComparison.Ordinal));
        }
    }
}
