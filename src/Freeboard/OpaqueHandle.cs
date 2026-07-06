using System.Security.Cryptography;

namespace Freeboard;

/// <summary>
/// Mints an opaque, unguessable handle: 128 bits of CSPRNG entropy as a hex string. Used as the
/// correlation key/nonce for the short-lived in-process stores (pending MFA, WebAuthn enrollment,
/// one-time credential display). Centralised so the entropy width and encoding cannot drift between
/// those stores.
/// </summary>
public static class OpaqueHandle
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
