namespace Freeboard.Persistence;

/// <summary>
/// Read abstraction over persisted machine assets. Every lookup is filtered by <c>organisation_id</c>, so
/// no read returns an asset from another organisation. Exposes lookups only - no mutating method - so state
/// changes only through <see cref="IAssetWriteStore"/>.
/// </summary>
public interface IAssetStore
{
    /// <summary>Returns the asset with <paramref name="assetId"/> in the organisation, or null.</summary>
    Task<AssetRow?> GetByIdAsync(
        string organisationId, string assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the asset whose canonical identity is <c>(identityKind, identityValue)</c> in the
    /// organisation, or null.
    /// </summary>
    Task<AssetRow?> GetByIdentityAsync(
        string organisationId, string identityKind, string identityValue, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the asset the source <c>(source, externalId)</c> is attached to in the organisation, or
    /// null.
    /// </summary>
    Task<AssetRow?> GetBySourceAsync(
        string organisationId, string source, string externalId, CancellationToken cancellationToken = default);
}
