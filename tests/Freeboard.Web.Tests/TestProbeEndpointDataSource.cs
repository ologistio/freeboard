using Freeboard.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;

namespace Freeboard.Web.Tests;

/// <summary>
/// Registers a single test-only unmarked mutating route - <c>POST /api/v1/freeboard/_probe</c> -
/// as an <see cref="EndpointDataSource"/> so it is matched by the app's routing and flows through
/// the real GitOps read-only middleware. This replaces the production <c>/_probe</c> route:
/// the route-move test needs a non-auth mutating route under the API prefix to prove the
/// read-only exemption is scoped to AuthEndpoint-marked routes, but that route must not ship in
/// Program.cs. Adding an EndpointDataSource via the test factory keeps it test-only while still
/// exercising the real pipeline. The endpoint carries NO AuthEndpoint marker, so it must still 409.
/// </summary>
internal sealed class TestProbeEndpointDataSource : EndpointDataSource
{
    public const string Path = ApiRoutes.ApiRoutePrefix + "/_probe";

    private readonly List<Endpoint> _endpoints;

    public TestProbeEndpointDataSource()
    {
        var builder = new RouteEndpointBuilder(
            requestDelegate: static ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return ctx.Response.WriteAsync("{\"ok\":true}");
            },
            routePattern: RoutePatternFactory.Parse(Path),
            order: 0)
        {
            DisplayName = "TEST POST /_probe",
        };
        builder.Metadata.Add(new HttpMethodMetadata(["POST"]));
        _endpoints = [builder.Build()];
    }

    public override IReadOnlyList<Endpoint> Endpoints => _endpoints;

    // The endpoint set is fixed for the test host's lifetime: a token that never fires.
    public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
}
