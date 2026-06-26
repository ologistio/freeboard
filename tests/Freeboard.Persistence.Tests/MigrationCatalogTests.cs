using Freeboard.Persistence.System;

namespace Freeboard.Persistence.Tests;

public sealed class MigrationCatalogTests
{
    [Fact]
    public void LoadOrdersByNumericOrdinalAcross001_002_010()
    {
        // The test assembly embeds 001_first, 002_second, 010_tenth (and a 020_broken
        // fixture used by the failed-migration integration test).
        var migrations = MigrationCatalog.Load(typeof(MigrationCatalogTests).Assembly);

        var versions = migrations.Select(m => m.Version).ToList();
        Assert.Equal(["001_first", "002_second", "010_tenth", "020_broken"], versions);
        Assert.Equal([1, 2, 10, 20], migrations.Select(m => m.Ordinal).ToList());
    }

    [Fact]
    public void ChecksumIsStableLowercaseHex64()
    {
        var checksum = MigrationCatalog.Checksum("CREATE TABLE t (id INT);");

        Assert.Equal(64, checksum.Length);
        Assert.Equal(checksum.ToLowerInvariant(), checksum);
        Assert.Equal(MigrationCatalog.Checksum("CREATE TABLE t (id INT);"), checksum);
        Assert.NotEqual(MigrationCatalog.Checksum("CREATE TABLE t (id INT);"), MigrationCatalog.Checksum("other"));
    }

    [Fact]
    public void RealAssemblyContainsInitialSchema()
    {
        var migrations = MigrationCatalog.Load(typeof(IMigrationRunner).Assembly);

        Assert.Contains(migrations, m => m.Version == "001_initial_schema");
    }
}
