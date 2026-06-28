using Freeboard.Persistence;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Microsoft.Extensions.DependencyInjection;

namespace Freeboard.Web.Tests;

public sealed class ComplianceDiTests
{
    [Fact]
    public void WebResolvesReaderButNotImporterOrMigrationRunner()
    {
        using var factory = new ComplianceWebFactory(new FakeComplianceStore());
        var services = factory.Services;

        Assert.NotNull(services.GetService<IComplianceStore>());
        Assert.Null(services.GetService<IGitOpsImporter>());
        Assert.Null(services.GetService<IMigrationRunner>());
    }
}
