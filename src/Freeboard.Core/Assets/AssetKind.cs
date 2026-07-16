namespace Freeboard.Core.Assets;

/// <summary>
/// The kind of asset. One asset model spans the declared estate (<see cref="Company"/>,
/// <see cref="Department"/>, <see cref="Vendor"/>) and the discovered estate (<see cref="Machine"/>).
/// </summary>
public enum AssetKind
{
    Company,
    Department,
    Machine,
    Vendor,
}

/// <summary>
/// How an asset entered the store. <see cref="Declared"/> assets are authored in gitops config and
/// reconciled by sync; <see cref="Discovered"/> assets are written by ingest and never touched by sync.
/// </summary>
public enum AssetSource
{
    Declared,
    Discovered,
}

/// <summary>
/// A machine's lifecycle state. A newly observed machine is <see cref="Seen"/>; retirement is a state
/// change to <see cref="Retired"/>, not a delete, and re-observation returns it to <see cref="Seen"/>.
/// </summary>
public enum AssetState
{
    Seen,
    Retired,
}
