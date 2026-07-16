namespace Freeboard.Persistence;

/// <summary>
/// A persisted machine asset: one resolved discovered machine from the unified <c>assets</c> table.
/// Identity is <see cref="Id"/> (a ULID). <see cref="Type"/> is the asset type (<c>Machine</c>) and
/// <see cref="Source"/> is <c>discovered</c>. <see cref="Parent"/> is the containing organisation id, a
/// scalar reference with no FK so an asset outlives a removed organisation; <see cref="Owner"/> is unused
/// for a machine. <see cref="IdentityKind"/> (<c>Serial</c> or <c>HostUuid</c>) and
/// <see cref="IdentityValue"/> are the resolved canonical identity; <see cref="State"/> is <c>Seen</c> or
/// <c>Retired</c>. <see cref="Hostname"/> is display-only and null when unset. <see cref="RetiredAt"/> is
/// null while the machine is <c>Seen</c>.
/// </summary>
public sealed record AssetRow(
    string Id,
    string Type,
    string Source,
    string? Parent,
    string? Owner,
    string IdentityKind,
    string IdentityValue,
    string? Hostname,
    string State,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    DateTime? RetiredAt,
    DateTime CreatedAt);

/// <summary>
/// What one source observed about a machine. <see cref="Source"/> is the integration token (e.g.
/// <c>fleetdm</c>) and <see cref="ExternalId"/> is that source's stable id for the machine; together they
/// are the per-org source key. <see cref="HardwareSerial"/> and <see cref="HostUuid"/> are the raw observed
/// identifiers the store derives the canonical identity from (in Core); both may be null. <see cref="Hostname"/>
/// is an optional display name.
/// </summary>
public sealed record NewMachineObservation(
    string OrganisationId,
    string Source,
    string ExternalId,
    string? HardwareSerial,
    string? HostUuid,
    string? Hostname);

/// <summary>The outcome axis of an <see cref="AssetUpsertResult"/>.</summary>
public enum AssetUpsertStatus
{
    Created,
    Updated,
    Conflict,
    Invalid,
}

/// <summary>
/// The outcome of a resolve-and-attach upsert. <see cref="AssetId"/> is the resolved asset id on
/// <c>Created</c>/<c>Updated</c> and null otherwise. <see cref="Error"/> names the reason on
/// <c>Conflict</c>/<c>Invalid</c> and is null on success. A <c>Conflict</c> (source relink or cross-axis
/// collision) and an <c>Invalid</c> (any input-validation failure: a missing required key, an over-long
/// field, or no derivable identity) both write nothing.
/// </summary>
public sealed record AssetUpsertResult(AssetUpsertStatus Status, string? AssetId, string? Error)
{
    public static AssetUpsertResult Created(string assetId) => new(AssetUpsertStatus.Created, assetId, null);

    public static AssetUpsertResult Updated(string assetId) => new(AssetUpsertStatus.Updated, assetId, null);

    public static AssetUpsertResult Conflict(string error) => new(AssetUpsertStatus.Conflict, null, error);

    public static AssetUpsertResult Invalid(string error) => new(AssetUpsertStatus.Invalid, null, error);
}
