using Microsoft.AspNetCore.Hosting;

namespace Freeboard.Web.Tests;

/// <summary>
/// Supplies valid (test-only, non-secret) auth crypto config so the web host's AddAuth key
/// validation passes at startup. The web app validates all three key sets eagerly, so
/// every WebApplicationFactory that boots the real Program must provide them.
/// </summary>
internal static class AuthTestConfig
{
    // 32 bytes of 0x41 ('A'), base64. Test-only: never a real secret.
    private const string Key32 = "QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUE=";

    public static void Apply(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:PasswordSecrets:1", Key32);
        builder.UseSetting("Auth:CurrentPasswordSecretVersion", "1");
        builder.UseSetting("Auth:TokenKeys:1", Key32);
        builder.UseSetting("Auth:CurrentTokenKeyVersion", "1");
        builder.UseSetting("Auth:SecretProtectionKeys:1", Key32);
        builder.UseSetting("Auth:CurrentSecretProtectionKeyVersion", "1");
    }
}
