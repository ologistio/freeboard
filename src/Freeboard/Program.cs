using Fido2NetLib;
using Freeboard.Api;
using Freeboard.Auth;
using Freeboard.Compliance;
using Freeboard.GitOps;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GitOpsOptions>(builder.Configuration.GetSection(GitOpsOptions.SectionName));
builder.Services.Configure<WebAuthOptions>(builder.Configuration.GetSection(WebAuthOptions.SectionName));

var freeboardConnectionString = builder.Configuration.GetConnectionString("Freeboard") ?? string.Empty;

// Reader only. The web app never registers the importer or migration runner and
// never auto-connects, auto-migrates, or auto-syncs. The connection is opened lazily
// per request; an empty/missing connection string surfaces as an unreachable store at
// request time, not a startup crash.
builder.Services.AddComplianceStore(freeboardConnectionString);

// Full auth stack: stores, hasher, token hasher, secret protector, ULID factory, password
// reset store. Crypto material is bound from the "Auth" config section and validated eagerly
// (a misconfigured deployment fails at startup). The connection factory is shared with
// the compliance store (registered once via TryAdd).
builder.Services.AddAuth(freeboardConnectionString, builder.Configuration);

// Trusted-proxy forwarded headers: the client IP for rate limiting and the WebAuthn
// origin are only trustworthy behind a configured proxy. Read the configured proxies/networks
// once. ONLY when at least one is configured do we add them to the trust list and apply
// UseForwardedHeaders later. When NONE are configured we do NOT process forwarded headers at all,
// so an attacker cannot spoof X-Forwarded-For to defeat per-IP rate limiting; the socket
// RemoteIpAddress is used instead.
var configuredProxies = (builder.Configuration.GetSection("Auth:ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    .Select(p => System.Net.IPAddress.TryParse(p, out var ip) ? ip : null)
    .Where(ip => ip is not null)
    .Cast<System.Net.IPAddress>()
    .ToList();
var configuredNetworks = (builder.Configuration.GetSection("Auth:ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    .Select(n => System.Net.IPNetwork.TryParse(n, out var net) ? (System.Net.IPNetwork?)net : null)
    .Where(net => net is not null)
    .Select(net => net!.Value)
    .ToList();
var trustForwardedHeaders = configuredProxies.Count > 0 || configuredNetworks.Count > 0;

if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        foreach (var ip in configuredProxies)
        {
            o.KnownProxies.Add(ip);
        }

        foreach (var net in configuredNetworks)
        {
            o.KnownIPNetworks.Add(net);
        }
    });
}

builder.Services.AddSingleton<AuthRateLimiter>();
builder.Services.AddSingleton<SessionIssuer>();
builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IAuthorizationHandler, RequireSudoModeHandler>());

// WebAuthn/FIDO2. RP id + origins are EXPLICIT REQUIRED outside Development; the
// ceremony service exposes IsConfigured and the passkey endpoints refuse to run unconfigured
// outside dev. The underlying library validates origin/RP-id on both registration and assertion.
builder.Services.Configure<WebAuthnOptions>(builder.Configuration.GetSection(WebAuthnOptions.SectionName));
var webAuthnOptions = builder.Configuration.GetSection(WebAuthnOptions.SectionName).Get<WebAuthnOptions>() ?? new WebAuthnOptions();
if (!builder.Environment.IsDevelopment()
    && (string.IsNullOrEmpty(webAuthnOptions.RpId) || webAuthnOptions.Origins.Length == 0))
{
    throw new InvalidOperationException(
        "Auth:WebAuthn:RpId and Auth:WebAuthn:Origins are REQUIRED outside Development.");
}

builder.Services.AddFido2(o =>
{
    o.ServerDomain = string.IsNullOrEmpty(webAuthnOptions.RpId) ? "localhost" : webAuthnOptions.RpId;
    o.ServerName = webAuthnOptions.RpName;
    o.Origins = webAuthnOptions.Origins.Length > 0
        ? new HashSet<string>(webAuthnOptions.Origins, StringComparer.Ordinal)
        : new HashSet<string> { "https://localhost" };
});
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<WebAuthnEnrollmentStore>();
// WebAuthnCeremony depends on the scoped IFido2, so it (and the services that consume it) are
// scoped. The auth stores are singletons but resolve fine from a scoped service.
builder.Services.AddScoped<WebAuthnCeremony>();
builder.Services.AddScoped<MfaFactorService>();
builder.Services.AddScoped<MfaChallengeService>();

// Generic email transport, selected by Email:Transport. Registers the matching IEmailSender and
// fails fast on an smtp transport that could not deliver. AuthEmailService (which builds the auth
// messages and owns the auth base URL) is registered only when a sender is present, so the
// optional-presence contract the auth endpoints depend on is keyed on a configured transport.
var emailOptions = Freeboard.Email.EmailRegistration.Add(builder.Services, builder.Configuration);
if (emailOptions.Transport != Freeboard.Email.EmailTransport.None)
{
    var authBaseUrl = builder.Configuration["Auth:Email:BaseUrl"] ?? string.Empty;
    builder.Services.AddSingleton(sp =>
        new AuthEmailService(sp.GetRequiredService<Freeboard.Core.Email.IEmailSender>(), authBaseUrl));
}

builder.Services.AddAuthentication(AuthClaims.Scheme)
    .AddScheme<AuthenticationSchemeOptions, BearerAuthenticationHandler>(AuthClaims.Scheme, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(RequireSudoModeRequirement.PolicyName, policy =>
    {
        policy.AddAuthenticationSchemes(AuthClaims.Scheme);
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new RequireSudoModeRequirement());
    })
    .AddPolicy(GlobalRoles.AdminPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(AuthClaims.Scheme);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(AuthClaims.Role, GlobalRoles.Admin);
    });

var app = builder.Build();

// Startup fail-fast: if the password-reset flow is enabled but no AuthEmailService is registered
// (email transport is none), forgot-password could not deliver a token, which would make its
// behavior non-uniform. Fail at startup so forgot-password is always a uniform 200. Checked against
// the built provider so any registration is visible.
var webAuth = app.Services.GetRequiredService<IOptions<WebAuthOptions>>().Value;
if (webAuth.PasswordResetEnabled && app.Services.GetService<AuthEmailService>() is null)
{
    throw new InvalidOperationException(
        "Auth:PasswordResetEnabled is true but no email transport is configured. Set Email:Transport or disable password reset.");
}

// Construct AuthEmailService now so its Auth:Email:BaseUrl validation fails fast at startup rather
// than lazily on the first send. The singleton factory is otherwise not invoked until first resolved,
// which (with password reset disabled) would defer an invalid base URL to a magic-link send.
if (emailOptions.Transport != Freeboard.Email.EmailTransport.None)
{
    _ = app.Services.GetRequiredService<AuthEmailService>();
}

// The log transport is a non-delivering development sink. Warn loudly at startup so it is never
// mistaken for a working transport in production.
if (emailOptions.Transport == Freeboard.Email.EmailTransport.Log)
{
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Freeboard.Email").LogWarning(
        "The 'log' email transport is a non-delivering development sink and must not be used in production.");
}

// Only honor forwarded headers when trusted proxies/networks are configured. With none
// configured, the socket RemoteIpAddress is used and X-Forwarded-For is ignored.
if (trustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

// UseRouting BEFORE the read-only middleware so context.GetEndpoint() is populated and
// the middleware can read the AuthEndpoint marker. Endpoint mapping stays after it.
app.UseRouting();

app.UseMiddleware<GitOpsReadOnlyMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// Enforce the force-reset (limited) session allowlist after auth.
app.UseMiddleware<LimitedSessionGuardMiddleware>();

app.MapGet("/", () => "Hello World!");

app.MapComplianceEndpoints();

app.MapGet(ApiRoutes.ApiRoutePrefix + "/gitops/status", (IOptions<GitOpsOptions> options) =>
{
    var gitops = options.Value;
    return string.IsNullOrEmpty(gitops.RepositoryUrl)
        ? Results.Ok(new { gitOps = gitops.ReadOnly })
        : Results.Ok(new { gitOps = gitops.ReadOnly, repositoryUrl = gitops.RepositoryUrl });
});

app.MapAuthEndpoints();
app.MapUserAdminEndpoints();
app.MapMfaLoginEndpoints();
app.MapMfaEnrollmentEndpoints();
app.MapSudoEndpoints();

app.Run();

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
