namespace Freeboard.Core.Assets;

/// <summary>The kind of asset. Asset resolution is machine-scoped, so <see cref="Machine"/> is the only kind.</summary>
public enum AssetKind
{
    Machine,
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
