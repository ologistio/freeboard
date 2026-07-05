namespace Freeboard.Authz;

/// <summary>
/// The staged rollout mode, read from <c>Authz:Mode</c>. It governs READ narrowing only; every
/// mutating route force-enforces in every mode.
/// </summary>
public enum AuthzMode
{
    /// <summary>Decisions are observed and audited but org-scoped compliance reads are not narrowed.</summary>
    Observe,

    /// <summary>Grant-holders are narrowed; a zero-grant caller keeps a full audited legacy read fallback.</summary>
    Compat,

    /// <summary>Strict: no legacy fallback; a zero-grant caller sees nothing.</summary>
    Enforce,
}

/// <summary>The resolved rollout mode as an injectable singleton.</summary>
public sealed class AuthzRuntimeOptions
{
    public AuthzMode Mode { get; init; } = AuthzMode.Compat;

    /// <summary>Parses <c>Authz:Mode</c>, defaulting to <see cref="AuthzMode.Compat"/> on absent/invalid.</summary>
    public static AuthzMode Parse(string? value)
        => Enum.TryParse<AuthzMode>(value, ignoreCase: true, out var mode) ? mode : AuthzMode.Compat;
}
