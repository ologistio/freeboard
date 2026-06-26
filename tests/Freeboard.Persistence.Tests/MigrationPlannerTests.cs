using Freeboard.Persistence.System;

namespace Freeboard.Persistence.Tests;

public sealed class MigrationPlannerTests
{
    private static readonly EmbeddedMigration M1 = new("001_a", 1, "sql-a", "csum-a");
    private static readonly EmbeddedMigration M2 = new("002_b", 2, "sql-b", "csum-b");

    [Fact]
    public void ClassifyReportsAllPendingWhenNoneRecorded()
    {
        var state = MigrationPlanner.Classify([M1, M2], []);

        Assert.Empty(state.Applied);
        Assert.Equal(["001_a", "002_b"], state.Pending);
        Assert.False(state.IsCurrent);
    }

    [Fact]
    public void ClassifySplitsAppliedAndPending()
    {
        var state = MigrationPlanner.Classify([M1, M2], [new AppliedMigration("001_a", "csum-a")]);

        Assert.Equal(["001_a"], state.Applied);
        Assert.Equal(["002_b"], state.Pending);
    }

    [Fact]
    public void ClassifyIsCurrentWhenAllApplied()
    {
        var state = MigrationPlanner.Classify(
            [M1, M2],
            [new AppliedMigration("001_a", "csum-a"), new AppliedMigration("002_b", "csum-b")]);

        Assert.True(state.IsCurrent);
        Assert.Empty(state.Pending);
    }

    [Fact]
    public void ValidatePassesWhenChecksumsMatch()
    {
        MigrationPlanner.Validate([M1], [new AppliedMigration("001_a", "csum-a")]);
    }

    [Fact]
    public void ValidateFailsOnChecksumMismatch()
    {
        var ex = Assert.Throws<MigrationException>(() =>
            MigrationPlanner.Validate([M1], [new AppliedMigration("001_a", "different")]));

        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateFailsOnRecordedButMissingMigration()
    {
        // 002_b recorded as applied but absent from the embedded set (deleted/renamed).
        var ex = Assert.Throws<MigrationException>(() =>
            MigrationPlanner.Validate([M1], [new AppliedMigration("002_b", "csum-b")]));

        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingMigrationIsDistinctFromChecksumMismatch()
    {
        var missing = Assert.Throws<MigrationException>(() =>
            MigrationPlanner.Validate([M1], [new AppliedMigration("999_gone", "x")]));
        var mismatch = Assert.Throws<MigrationException>(() =>
            MigrationPlanner.Validate([M1], [new AppliedMigration("001_a", "x")]));

        Assert.NotEqual(missing.Message, mismatch.Message);
    }
}
