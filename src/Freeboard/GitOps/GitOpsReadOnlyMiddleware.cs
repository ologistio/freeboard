using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Freeboard.GitOps;

/// <summary>
/// When GitOps read-only mode is on, rejects mutating HTTP methods
/// (POST/PUT/PATCH/DELETE) with 409 Conflict and an RFC 7807 problem-details
/// body. GET/HEAD/OPTIONS pass through. Enforcement is server-side.
/// </summary>
public sealed class GitOpsReadOnlyMiddleware(RequestDelegate next, IOptions<GitOpsOptions> options)
{
    private static readonly HashSet<string> MutatingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
    };

    private readonly GitOpsOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.ReadOnly || !MutatingMethods.Contains(context.Request.Method))
        {
            await next(context);
            return;
        }

        var detail = "This instance is GitOps-managed. Changes must be made in the git repository";
        var hasRepo = !string.IsNullOrEmpty(_options.RepositoryUrl);
        if (hasRepo)
        {
            detail += $": {_options.RepositoryUrl}";
        }

        detail += ".";

        var body = new Dictionary<string, object>
        {
            ["type"] = "https://freeboard.io/problems/gitops-read-only",
            ["title"] = "GitOps read-only mode",
            ["status"] = StatusCodes.Status409Conflict,
            ["detail"] = detail,
        };

        if (hasRepo)
        {
            body["repositoryUrl"] = _options.RepositoryUrl;
        }

        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}
