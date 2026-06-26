using Freeboard.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Freeboard.Web.Tests;

/// <summary>
/// Builds the web app with an injected <see cref="IComplianceStore"/> double so web
/// tests need no MySQL. Replaces the real store registration via ConfigureTestServices.
/// </summary>
internal sealed class ComplianceWebFactory(IComplianceStore store, bool readOnly = false)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Freeboard:GitOps:ReadOnly", readOnly ? "true" : "false");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IComplianceStore>();
            services.AddSingleton(store);
        });
    }
}
