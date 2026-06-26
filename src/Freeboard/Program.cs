using Freeboard.Compliance;
using Freeboard.GitOps;
using Freeboard.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GitOpsOptions>(builder.Configuration.GetSection(GitOpsOptions.SectionName));

// Reader only. The web app never registers the importer or migration runner and
// never auto-connects, auto-migrates, or auto-syncs. The connection is opened lazily
// per request; an empty/missing connection string surfaces as an unreachable store at
// request time, not a startup crash.
builder.Services.AddComplianceStore(
    builder.Configuration.GetConnectionString("Freeboard") ?? string.Empty);

var app = builder.Build();

app.UseMiddleware<GitOpsReadOnlyMiddleware>();

app.MapGet("/", () => "Hello World!");

app.MapComplianceEndpoints();

app.MapGet("/api/gitops/status", (Microsoft.Extensions.Options.IOptions<GitOpsOptions> options) =>
{
    var gitops = options.Value;
    return string.IsNullOrEmpty(gitops.RepositoryUrl)
        ? Results.Ok(new { gitOps = gitops.ReadOnly })
        : Results.Ok(new { gitOps = gitops.ReadOnly, repositoryUrl = gitops.RepositoryUrl });
});

app.Run();

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
