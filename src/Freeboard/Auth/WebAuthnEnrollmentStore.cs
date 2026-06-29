using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Freeboard.Auth;

/// <summary>
/// A short-lived, in-process store for in-flight WebAuthn ENROLLMENT (registration) options,
/// keyed by an opaque correlation token returned to the client and presented on completion. Login
/// ASSERTION options instead ride on the persisted mfa_login_challenges row (multi-instance safe);
/// enrollment is a bearer-authenticated, immediate round-trip so an in-process cache is adequate
/// (a multi-instance deployment behind a sticky LB, or a later distributed-cache swap, covers scale).
/// </summary>
public sealed class WebAuthnEnrollmentStore(IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>Stores the options JSON for a user and returns the correlation token.</summary>
    public string Stash(string userId, string optionsJson)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        cache.Set(Key(userId, token), optionsJson, Ttl);
        return token;
    }

    /// <summary>Retrieves and removes the options JSON for a user + token, or null if absent/expired.</summary>
    public string? Take(string userId, string token)
    {
        var key = Key(userId, token);
        if (cache.TryGetValue(key, out string? json))
        {
            cache.Remove(key);
            return json;
        }

        return null;
    }

    private static string Key(string userId, string token) => $"webauthn-enroll:{userId}:{token}";
}
