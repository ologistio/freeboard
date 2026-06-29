using System.Net.Http.Headers;
using Freeboard.Auth;
using Freeboard.Core.Email;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Freeboard.Web.Tests;

/// <summary>
/// Boots the real web app with in-memory auth fakes so the auth endpoints, bearer handler, and
/// rate limiter run without MySQL. The real ITokenHasher (test keys) is kept so minted tokens
/// hash exactly as the handler expects. Exposes the fakes and a helper to create an
/// authenticated client by seeding a user + session and minting its bearer token.
/// </summary>
internal sealed class AuthWebFactory : WebApplicationFactory<Program>
{
    public AuthWebFactory()
    {
        // Wire the credential fake to the session/user fakes so the combined transactional method
        // actually revokes sessions / flips force-reset in tests.
        Credentials = new FakeCredentialStore(Sessions, Users);
    }

    public FakeSessionStore Sessions { get; } = new();

    public FakeUserStore Users { get; } = new();

    public FakeCredentialStore Credentials { get; }

    public FakeResetStore Resets { get; } = new();

    public FakePasswordHasher Hasher { get; } = new();

    public RecordingEmailSender Email { get; } = new();

    /// <summary>The auth base URL the registered AuthEmailService builds links from.</summary>
    public const string AuthBaseUrl = "https://freeboard.example";

    public FakeRateLimitStore RateLimit { get; } = new();

    public FakeMfaChallengeStore Challenges { get; private set; } = new();

    public FakeTotpStore Totp { get; } = new();

    public FakeRecoveryCodeStore Recovery { get; } = new();

    public FakeWebAuthnCredentialStore WebAuthn { get; } = new();

    /// <summary>
    /// Captures every log entry the app emits at information level and above, so a test can assert a
    /// credential (e.g. a reset token) never reaches the logs. Each entry records both the rendered
    /// message and the exception's full string - a real provider renders the exception separately, so
    /// a leak hiding in an attached exception object must be caught too.
    /// </summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>When true, AddAuth's email-sender seam is registered (for password-reset tests).</summary>
    public bool RegisterEmailSender { get; init; }

    /// <summary>
    /// When true, the registered sender throws on send. Used to prove forgot-password stays a
    /// uniform 200 when the reset email send fails (enumeration-safe on send failure).
    /// </summary>
    public bool EmailSenderThrows { get; init; }

    /// <summary>When true, the app boots in GitOps read-only mode.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The first-admin bootstrap secret to configure (empty disables setup).</summary>
    public string BootstrapSecret { get; init; } = string.Empty;

    /// <summary>
    /// When true, registers the test-only unmarked mutating route <c>POST /_probe</c> under the
    /// API prefix so the route-move/read-only test can exercise the AuthEndpoint scoping
    /// without shipping the route in Program.cs.
    /// </summary>
    public bool IncludeTestProbe { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development so the WebAuthn "required outside Development" gate is satisfied without real
        // RP config; passkey tests still supply RP id + origins below.
        builder.UseEnvironment("Development");
        AuthTestConfig.Apply(builder);
        builder.UseSetting("Freeboard:GitOps:ReadOnly", ReadOnly ? "true" : "false");
        builder.UseSetting("Auth:BootstrapSecret", BootstrapSecret);
        builder.UseSetting("Auth:PasswordResetEnabled", RegisterEmailSender ? "true" : "false");
        builder.UseSetting("Auth:WebAuthn:RpId", "localhost");
        builder.UseSetting("Auth:WebAuthn:Origins:0", "https://localhost");

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IComplianceStore>();
            services.AddSingleton<IComplianceStore>(new FakeComplianceStore());

            if (IncludeTestProbe)
            {
                services.AddSingleton<EndpointDataSource>(new TestProbeEndpointDataSource());
            }

            Replace<ISessionStore>(services, Sessions);
            Replace<IUserStore>(services, Users);
            Replace<IPasswordCredentialStore>(services, Credentials);
            Replace<IPasswordResetStore>(services, Resets);
            Replace<IPasswordHasher>(services, Hasher);
            Replace<IAuthRateLimitStore>(services, RateLimit);

            // MFA stores. The challenge store needs the real ITokenHasher for magic-link verify, so
            // build it from the provider once available.
            services.RemoveAll<IMfaChallengeStore>();
            services.AddSingleton<IMfaChallengeStore>(provider =>
            {
                Challenges = new FakeMfaChallengeStore(provider.GetRequiredService<ITokenHasher>());
                return Challenges;
            });
            Replace<ITotpStore>(services, Totp);
            Replace<IRecoveryCodeStore>(services, Recovery);
            Replace<IWebAuthnCredentialStore>(services, WebAuthn);

            if (RegisterEmailSender)
            {
                // Register a REAL AuthEmailService (it builds the messages and validates the base
                // URL) backed by a recording or throwing IEmailSender, so the endpoint tests exercise
                // the production message-building path.
                services.RemoveAll<IEmailSender>();
                services.RemoveAll<AuthEmailService>();
                IEmailSender sender = EmailSenderThrows ? new ThrowingEmailSender() : Email;
                services.AddSingleton(sender);
                services.AddSingleton(new AuthEmailService(sender, AuthBaseUrl));
            }
        });
    }

    private static void Replace<T>(IServiceCollection services, T instance)
        where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }

    /// <summary>
    /// Seeds a user (with a credential under <paramref name="password"/>) and a session, then
    /// returns a client whose Authorization header carries the session's bearer token.
    /// </summary>
    public HttpClient CreateAuthenticatedClient(
        UserRow user, SessionAuthState authState = SessionAuthState.Full, string password = "password")
    {
        Users.Add(user);
        Credentials.SetAsync(user.Id, Hasher.Hash(password), 1).GetAwaiter().GetResult();
        var credentialVersion = Credentials.GetAsync(user.Id).GetAwaiter().GetResult()!.CredentialVersion;

        var hasher = Services.GetRequiredService<ITokenHasher>();
        var minted = hasher.MintPrefixed();
        Sessions.Add(
            new SessionRow($"sess-{user.Id}", user.Id, minted.KeyVersion, authState, credentialVersion, null,
                DateTime.UtcNow, DateTime.UtcNow.AddHours(1), null),
            minted.Hash);

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", minted.Token);
        return client;
    }

    public static UserRow MakeUser(
        string id, string role = "member", bool enabled = true, bool forcePasswordReset = false, bool mfaEnabled = false)
        => new(id, $"{id}@example.com", $"{id}@example.com", id, role, enabled, forcePasswordReset, mfaEnabled,
            DateTime.UtcNow, DateTime.UtcNow);
}

/// <summary>Captures app log entries at information level and above for assertion in tests.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Text)> _entries = new();

    /// <summary>Each entry's full text: the rendered message plus the exception string, if any.</summary>
    public IReadOnlyCollection<(LogLevel Level, string Text)> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(System.Collections.Concurrent.ConcurrentQueue<(LogLevel, string)> entries)
        : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Information)
            {
                return;
            }

            var text = formatter(state, exception);
            if (exception is not null)
            {
                text = $"{text} {exception}";
            }

            entries.Enqueue((logLevel, text));
        }
    }
}
