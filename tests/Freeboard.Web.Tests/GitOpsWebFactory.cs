using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// Builds the real web app for tests with the given GitOps config. Tests exercise the
/// real request pipeline so GitOpsReadOnlyMiddleware actually runs; no test-only
/// endpoint is injected (an injected endpoint mapped via a startup filter would sit
/// upstream of the middleware and make the off-test vacuous).
/// </summary>
internal sealed class GitOpsWebFactory(bool readOnly, string? repositoryUrl = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Freeboard:GitOps:ReadOnly", readOnly ? "true" : "false");
        AuthTestConfig.Apply(builder);
        if (repositoryUrl is not null)
        {
            builder.UseSetting("Freeboard:GitOps:RepositoryUrl", repositoryUrl);
        }
    }
}
