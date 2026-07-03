namespace Freeboard.Auth;

/// <summary>
/// Claim types and the scheme name for Freeboard bearer auth. The authenticated principal
/// carries the user id, session id, name, email, role, and the session auth-state so downstream
/// policies (sudo-mode, the limited-session allowlist, admin checks) and the app shell read them
/// without another lookup.
/// </summary>
public static class AuthClaims
{
    /// <summary>The bearer authentication scheme name.</summary>
    public const string Scheme = "FreeboardBearer";

    /// <summary>Claim type carrying the authenticated user's ULID id.</summary>
    public const string UserId = "freeboard:user_id";

    /// <summary>Claim type carrying the current session's ULID id.</summary>
    public const string SessionId = "freeboard:session_id";

    /// <summary>Claim type carrying the user's display name.</summary>
    public const string Name = "freeboard:name";

    /// <summary>Claim type carrying the user's email address.</summary>
    public const string Email = "freeboard:email";

    /// <summary>Claim type carrying the user's coarse global role.</summary>
    public const string Role = "freeboard:role";

    /// <summary>Claim type carrying the session auth-state (0 = full, 1 = force-reset-limited).</summary>
    public const string AuthState = "freeboard:auth_state";
}
