using Freeboard.Core.Email;
using Freeboard.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Freeboard.Web.Tests;

/// <summary>
/// Direct tests for the email registration seam used by Program.cs: which sender each transport
/// registers, and the smtp/unknown-transport fail-fast. Fast unit tests - no web host.
/// </summary>
public sealed class EmailRegistrationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static ServiceProvider Build(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        EmailRegistration.Add(services, config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TransportNoneRegistersNoSender()
    {
        // Default (no Transport set) and an explicit none both register nothing.
        Assert.Null(Build(Config()).GetService<IEmailSender>());
        Assert.Null(Build(Config(("Email:Transport", "none"))).GetService<IEmailSender>());
    }

    [Fact]
    public void TransportLogRegistersLoggingSender()
    {
        var sender = Build(Config(("Email:Transport", "log"))).GetService<IEmailSender>();
        Assert.IsType<LoggingEmailSender>(sender);
    }

    [Fact]
    public void TransportSmtpWithValidSettingsRegistersSmtpSender()
    {
        var sender = Build(Config(
            ("Email:Transport", "smtp"),
            ("Email:FromAddress", "noreply@freeboard.test"),
            ("Email:Smtp:Host", "smtp.example"))).GetService<IEmailSender>();
        Assert.IsType<SmtpEmailSender>(sender);
    }

    [Fact]
    public void UnknownTransportFailsFast()
    {
        // An unrecognised transport string must not silently register no sender.
        Assert.ThrowsAny<Exception>(() => Build(Config(("Email:Transport", "carrier-pigeon"))));
    }

    [Theory]
    // Missing or blank host.
    [InlineData("", "noreply@freeboard.test")]
    [InlineData("   ", "noreply@freeboard.test")]
    // Missing, blank, or unparseable from-address, including a token with no domain and a
    // display-name form.
    [InlineData("smtp.example", "")]
    [InlineData("smtp.example", "   ")]
    [InlineData("smtp.example", "a@b@c")]
    [InlineData("smtp.example", "not-an-address")]
    [InlineData("smtp.example", "noreply")]
    [InlineData("smtp.example", "Freeboard <noreply@freeboard.test>")]
    public void SmtpWithIncompleteDeliverySettingsFailsFast(string host, string fromAddress)
    {
        Assert.Throws<InvalidOperationException>(() => Build(Config(
            ("Email:Transport", "smtp"),
            ("Email:FromAddress", fromAddress),
            ("Email:Smtp:Host", host))));
    }

    [Theory]
    // Missing, blank, or not an absolute http(s) URL (scheme-less, rooted-relative, or non-web scheme).
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("freeboard.example")]
    [InlineData("/reset-password")]
    [InlineData("ftp://freeboard.example")]
    public void AuthEmailServiceRejectsInvalidBaseUrl(string baseUrl)
    {
        // AuthEmailService owns the auth-link base URL and validates it eagerly, so a registered
        // service with an invalid Auth:Email:BaseUrl fails fast at startup.
        Assert.Throws<InvalidOperationException>(
            () => new Freeboard.Auth.AuthEmailService(new NullEmailSender(), baseUrl));
    }

    [Fact]
    public void AuthEmailServiceAcceptsAbsoluteHttpsBaseUrl()
    {
        // A valid absolute URL constructs without throwing.
        _ = new Freeboard.Auth.AuthEmailService(new NullEmailSender(), "https://freeboard.example");
    }

    [Fact]
    public void PasswordResetEnabledWithNoTransportFailsFast()
    {
        // Program.cs runs this cross-cutting check against the built provider: enabling password
        // reset with no email transport registers no AuthEmailService, which would make
        // forgot-password non-uniform, so the host must refuse to start.
        using var factory = new ResetWithoutSenderFactory();
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    /// <summary>Boots the real Program with password reset on but no email transport, to assert the startup throw.</summary>
    private sealed class ResetWithoutSenderFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            AuthTestConfig.Apply(builder);
            builder.UseSetting("Auth:WebAuthn:RpId", "localhost");
            builder.UseSetting("Auth:WebAuthn:Origins:0", "https://localhost");
            builder.UseSetting("Auth:PasswordResetEnabled", "true");
            builder.UseSetting("Email:Transport", "none");
        }
    }

    [Fact]
    public void TransportConfiguredWithInvalidBaseUrlFailsFastEvenWhenResetDisabled()
    {
        // A configured transport validates the auth base URL at startup even with password reset off:
        // Program.cs constructs AuthEmailService eagerly so an invalid Auth:Email:BaseUrl cannot defer
        // to the first magic-link send.
        using var factory = new InvalidBaseUrlFactory();
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    /// <summary>Boots Program with a transport configured, password reset off, and a non-absolute base URL.</summary>
    private sealed class InvalidBaseUrlFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            AuthTestConfig.Apply(builder);
            builder.UseSetting("Auth:WebAuthn:RpId", "localhost");
            builder.UseSetting("Auth:WebAuthn:Origins:0", "https://localhost");
            builder.UseSetting("Auth:PasswordResetEnabled", "false");
            builder.UseSetting("Email:Transport", "log");
            builder.UseSetting("Auth:Email:BaseUrl", "/reset-password");
        }
    }

    private sealed class NullEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
