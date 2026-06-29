namespace Freeboard.Persistence.Auth;

/// <summary>
/// Seam over ULID id generation. All Freeboard-generated auth ids are ULIDs
/// stored as Crockford base32 <c>CHAR(26) COLLATE utf8mb4_bin</c>. The seam keeps id
/// generation deterministic/controllable in tests.
/// </summary>
public interface IUlidFactory
{
    /// <summary>Generates a new time-ordered ULID and returns its 26-char Crockford base32 form.</summary>
    string NewId();

    /// <summary>
    /// Returns the canonical 26-char Crockford base32 form of <paramref name="value"/>,
    /// throwing if it is not a valid ULID. Used to normalize/validate ids from input
    /// before they reach the database.
    /// </summary>
    string Parse(string value);
}

/// <summary>Default factory backed by Cysharp <see cref="Ulid"/>.</summary>
public sealed class UlidFactory : IUlidFactory
{
    public string NewId() => Ulid.NewUlid().ToString();

    public string Parse(string value) => Ulid.Parse(value).ToString();
}
