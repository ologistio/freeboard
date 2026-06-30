using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Freeboard.Web.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Freeboard.WebE2E;

/// <summary>
/// Boots the real web app over an HTTPS Kestrel socket on a free localhost port, with the in-memory
/// auth fakes from <see cref="AuthWebFactory"/>, so a browser can drive the cookie-authenticated
/// pages. A real socket (not the in-memory TestServer) is required because the session cookie is
/// <c>__Host-</c> prefixed and Secure: it only sticks over a real HTTPS origin. The configured
/// WebAuthn RP id and origin are set to that exact origin so the passkey ceremony's origin check
/// passes against the CDP virtual authenticator.
///
/// .NET 10's <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> hosts
/// the app on Kestrel when <c>UseKestrel</c> is called before the host initializes. That makes the
/// SAME configured host (with this factory's <c>ConfigureWebHost</c> + <c>ConfigureTestServices</c>
/// fakes) listen on a real socket - so the singleton fake stores the seeding helpers write to are
/// exactly the ones the browser-driven request reads. There is no second, unconfigured host.
/// </summary>
internal sealed class E2EAppFixture : AuthWebFactory
{
    private readonly int _port = FreeTcpPort();
    private readonly X509Certificate2 _devCert = CreateSelfSignedLocalhostCert();

    /// <summary>The HTTPS origin the app listens on, e.g. <c>https://localhost:5xxxx</c>.</summary>
    public string BaseUrl => $"https://localhost:{_port}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Pin the WebAuthn RP id / origin to the exact Kestrel origin so the passkey ceremony's
        // origin check matches what the browser sends. The port is fixed up front (above) so the
        // origin string is known before the host builds.
        builder.UseSetting("Auth:WebAuthn:RpId", "localhost");
        builder.UseSetting("Auth:WebAuthn:Origins:0", BaseUrl);
    }

    /// <summary>
    /// Starts the configured factory host on a real HTTPS Kestrel socket (instead of the in-memory
    /// TestServer) so the browser can reach it. Must run before the host initializes; it is called
    /// from <see cref="EnsureStarted"/> before any client/seed touches <c>Services</c>.
    /// </summary>
    private void ConfigureKestrel()
        => UseKestrel(options => options.ListenLocalhost(_port, listen => listen.UseHttps(_devCert)));

    /// <summary>
    /// Builds and starts the single Kestrel-hosted app so its socket is listening and its DI is
    /// available for seeding before the browser navigates. Idempotent.
    /// </summary>
    public void EnsureStarted()
    {
        ConfigureKestrel();
        StartServer();

        // Confirm the host bound the pinned port; a mismatch would mean the browser and the seeded
        // app are not the same origin.
        var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        if (addresses is not null && !addresses.Addresses.Contains(BaseUrl))
        {
            throw new InvalidOperationException(
                $"Kestrel bound {string.Join(", ", addresses.Addresses)}, expected {BaseUrl}.");
        }
    }

    // The window between picking a free port and Kestrel binding it is a small TOCTOU race: another
    // process could grab the port. Standard pattern for ephemeral test ports; acceptable here.
    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// A self-signed localhost cert for dev HTTPS. Playwright ignores HTTPS errors for these tests,
    /// so the cert needs no trust chain - it only has to let Kestrel speak TLS.
    /// </summary>
    private static X509Certificate2 CreateSelfSignedLocalhostCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        // Round-trip through PFX so the private key is usable by Kestrel's TLS stack on all OSes.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _devCert.Dispose();
        }

        base.Dispose(disposing);
    }
}
