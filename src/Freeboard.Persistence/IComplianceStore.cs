namespace Freeboard.Persistence;

/// <summary>
/// The general read abstraction over the persisted compliance domain, and the only persistence surface
/// the web app depends on. Rows are ordered by <c>id</c> and each relation id array is ordered by id.
///
/// One decision names the lists it needs and gets ONE snapshot of them. A narrowed read decides what the
/// caller may see by resolving its rows against the <c>parent</c> and <c>owner</c> edges of the asset
/// rows, so rows and asset list read separately can pair pre-import edges with post-import rows - a
/// combination the database never held. Naming them together also records a decision's inputs on the
/// returned snapshot, where a test can assert them.
/// </summary>
public interface IComplianceStore
{
    /// <summary>
    /// Reads exactly the lists <paramref name="sets"/> names. A snapshot spanning more than one statement
    /// is read in one repeatable-read transaction, so it cannot straddle a concurrent importer commit. A
    /// one-statement snapshot runs without one, because one statement is already atomic. Reading a list
    /// the snapshot does not name throws <see cref="ComplianceReadSetNotRequestedException"/>.
    /// </summary>
    Task<ComplianceSnapshot> GetSnapshotAsync(
        ComplianceReadSet sets, CancellationToken cancellationToken = default);

    /// <summary>
    /// The per-kind counts. Separate from the snapshot read: it is one statement answering one question
    /// and it takes part in no narrowing, so it pairs with nothing.
    /// </summary>
    Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default);
}
