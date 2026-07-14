namespace Freeboard.Core.Assets;

/// <summary>Which axis a machine's resolved identity is on.</summary>
public enum MachineIdentityKind
{
    Serial,
    HostUuid,
}

/// <summary>
/// A machine's resolved identity: a normalized <see cref="Value"/> on one <see cref="Kind"/> axis.
/// Derivation is integration-agnostic domain logic shared by every source and lives here in Core (not
/// Persistence) so a collector and the store compute the same identity. Identity is single-axis: a usable
/// hardware serial is primary, else a usable host uuid, else there is no stable identity.
/// </summary>
public sealed record MachineIdentity(MachineIdentityKind Kind, string Value)
{
    // Common blank/OEM-filler serials that many unrelated machines report identically. Treated as absent
    // so distinct machines do not collapse into one identity. Compared against the normalized (trimmed,
    // whitespace-collapsed, upper-cased) serial. Held in code, not configuration: the set is small and
    // identical for every deployment.
    private static readonly IReadOnlySet<string> SerialPlaceholders = new HashSet<string>(StringComparer.Ordinal)
    {
        "UNKNOWN", "NONE", "NULL", "N/A", "NA", "DEFAULT STRING",
        "TO BE FILLED BY O.E.M.", "SYSTEM SERIAL NUMBER", "0",
    };

    // SMBIOS sentinel host uuids that firmware reports identically on many unrelated machines: the all-zero
    // uuid and the all-ones uuid. Treated as absent so distinct machines do not collapse onto one identity.
    private static readonly Guid AllOnesHostUuid = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    /// <summary>
    /// Derives the identity from an observed hardware serial and host uuid: the serial when usable, else
    /// the host uuid when usable, else null (no stable identity, reject the observation).
    /// </summary>
    public static MachineIdentity? Derive(string? hardwareSerial, string? hostUuid)
    {
        if (NormalizeSerial(hardwareSerial) is { } serial)
        {
            return new MachineIdentity(MachineIdentityKind.Serial, serial);
        }

        if (NormalizeHostUuid(hostUuid) is { } uuid)
        {
            return new MachineIdentity(MachineIdentityKind.HostUuid, uuid);
        }

        return null;
    }

    /// <summary>
    /// Normalizes a serial (trim, collapse internal whitespace, upper-case invariant) and returns null when
    /// it is blank or a known placeholder, so an unusable serial falls through to the host uuid.
    /// </summary>
    public static string? NormalizeSerial(string? hardwareSerial)
    {
        if (string.IsNullOrWhiteSpace(hardwareSerial))
        {
            return null;
        }

        var collapsed = string.Join(' ', hardwareSerial.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
        return SerialPlaceholders.Contains(collapsed) ? null : collapsed;
    }

    /// <summary>
    /// Normalizes a host uuid to its canonical lower-case hyphenated form so brace/case variants of one
    /// uuid collapse to one identity. Returns null when it does not parse as a uuid or is a firmware sentinel
    /// (all-zero or all-ones), so an unusable uuid is treated as absent rather than merging machines.
    /// </summary>
    public static string? NormalizeHostUuid(string? hostUuid)
    {
        if (string.IsNullOrWhiteSpace(hostUuid))
        {
            return null;
        }

        if (!Guid.TryParse(hostUuid, out var parsed) || parsed == Guid.Empty || parsed == AllOnesHostUuid)
        {
            return null;
        }

        return parsed.ToString("D");
    }
}
