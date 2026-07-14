namespace Freeboard.Persistence;

/// <summary>
/// Write abstraction over machine assets - the only path that changes asset state. The upsert resolves an
/// observation to a single asset by its canonical identity and attaches the source atomically; retirement
/// is a state change, not a delete.
/// </summary>
public interface IAssetWriteStore
{
    /// <summary>
    /// Resolves the observation to one asset by its derived identity and attaches the source in one
    /// transaction. Returns <c>Invalid</c> (writing nothing) when the observation fails input validation -
    /// a missing required key (organisation, source, or external id), an over-long field, or no derivable
    /// identity. Returns <c>Conflict</c> (writing nothing) when the source would relink to a different asset
    /// or a serial and uuid resolve to two different existing assets, else <c>Created</c> or <c>Updated</c>
    /// with the resolved asset id. Re-observing the same asset (including a retired one) is an <c>Updated</c>
    /// that returns it to <c>Seen</c>.
    /// </summary>
    Task<AssetUpsertResult> UpsertMachineFromSourceAsync(
        NewMachineObservation observation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires the asset: sets <c>state = Retired</c> and records <c>retired_at</c>, leaving the row and its
    /// sources persisted. A no-op (returning success) when no asset with that id exists in the organisation.
    /// </summary>
    Task<WriteResult> RetireAsync(
        string organisationId, string assetId, CancellationToken cancellationToken = default);
}
