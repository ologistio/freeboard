using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Freeboard.Web;

/// <summary>
/// Applies endpoint-metadata markers to Razor page routes by page name. The two auth middlewares
/// (force-reset guard, GitOps read-only) read these markers off the matched endpoint exactly as they
/// do for the minimal-API endpoints, so the page funnel reuses the existing marker mechanism instead
/// of a parallel path list.
/// </summary>
public static class PageEndpointMetadata
{
    /// <summary>
    /// Adds <paramref name="metadata"/> to the endpoint metadata of the named page's selectors. Page
    /// names are the route-relative paths Razor Pages assigns, e.g. <c>/Login</c>,
    /// <c>/Account/CompleteReset</c>.
    /// </summary>
    public static void AddPageMetadata(this PageConventionCollection conventions, string pageName, object metadata)
        => conventions.AddPageRouteModelConvention(pageName, model =>
        {
            foreach (var selector in model.Selectors)
            {
                selector.EndpointMetadata.Add(metadata);
            }
        });
}
