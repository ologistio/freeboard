using Freeboard.GitOps;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GitOpsOptions>(builder.Configuration.GetSection(GitOpsOptions.SectionName));

var app = builder.Build();

app.UseMiddleware<GitOpsReadOnlyMiddleware>();

app.MapGet("/", () => "Hello World!");

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
