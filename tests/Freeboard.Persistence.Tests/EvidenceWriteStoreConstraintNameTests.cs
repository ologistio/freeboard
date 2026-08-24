namespace Freeboard.Persistence.Tests;

/// <summary>
/// Unit tests for the constraint-name parser the evidence append uses to report a violated CHECK. The
/// store cannot reach a violation on its own, because it validates the same rules first, so no
/// integration test covers this text. The parser is pinned here instead: if MySQL changes the message
/// shape it degrades silently to a generic phrase, and only a test over the text catches that.
/// </summary>
public sealed class EvidenceWriteStoreConstraintNameTests
{
    [Fact]
    public void ParsesTheNameFromTheMessageMySqlRaises()
    {
        var name = MySqlEvidenceWriteStore.ConstraintName(
            "Check constraint 'ck_evidence_runs_error_detail' is violated.");

        Assert.Equal("ck_evidence_runs_error_detail", name);
    }

    [Fact]
    public void FallsBackToAGenericPhraseWhenTheMessageCarriesNoQuotedName()
    {
        Assert.Equal("on evidence_runs", MySqlEvidenceWriteStore.ConstraintName("Check constraint is violated."));
        Assert.Equal("on evidence_runs", MySqlEvidenceWriteStore.ConstraintName("Check constraint 'unterminated"));
    }
}
