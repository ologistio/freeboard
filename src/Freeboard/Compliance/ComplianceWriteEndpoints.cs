using System.Data.Common;
using Freeboard.Api;
using Freeboard.Auth;
using Freeboard.Persistence;

namespace Freeboard.Compliance;

/// <summary>
/// App-managed write endpoints for organisations and scope dispositions. Behind the admin
/// authorization policy, mirroring the admin write surface. Deliberately NOT marked as auth
/// endpoints, so the GitOps read-only middleware 409s them when the instance is in read-only mode
/// (that middleware runs before authentication, so read-only 409 takes precedence over a 401). Off
/// read-only mode they persist through <see cref="IComplianceWriteStore"/>, which enforces the same
/// invariants as import; an invalid write returns an RFC 7807 problem and changes nothing. A driver
/// failure that slips past the pre-checks maps to a problem body too: a unique-key violation (a
/// concurrent duplicate racing the pre-check) is a 409, any other store failure is a 503.
/// </summary>
public static class ComplianceWriteEndpoints
{
    // MySQL raises SQLSTATE 23000 for integrity-constraint violations (duplicate/unique key).
    private const string IntegrityConstraintSqlState = "23000";

    public sealed record OrganisationInput(string Id, string Title, string Kind, string? Parent);

    public sealed record ScopeInput(string Id, string Title, string Organisation, string Standard, string Disposition);

    public static void MapComplianceWriteEndpoints(this WebApplication app)
    {
        var writes = app.MapGroup(ApiRoutes.ApiRoutePrefix).RequireAuthorization(GlobalRoles.AdminPolicy);

        writes.MapPut("/organisations/{id}",
            (string id, OrganisationInput input, IComplianceWriteStore store, CancellationToken ct) =>
                RunAsync(() => store.UpsertOrganisationAsync(id, input.Title, input.Kind, input.Parent, ct)));

        writes.MapDelete("/organisations/{id}",
            (string id, IComplianceWriteStore store, CancellationToken ct) =>
                RunAsync(() => store.DeleteOrganisationAsync(id, ct)));

        writes.MapPut("/scopes/{id}",
            (string id, ScopeInput input, IComplianceWriteStore store, CancellationToken ct) =>
                RunAsync(() => store.UpsertScopeDispositionAsync(
                    id, input.Title, input.Organisation, input.Standard, input.Disposition, ct)));

        writes.MapDelete("/scopes/{id}",
            (string id, IComplianceWriteStore store, CancellationToken ct) =>
                RunAsync(() => store.DeleteScopeAsync(id, ct)));
    }

    private static async Task<IResult> RunAsync(Func<Task<WriteResult>> write)
    {
        try
        {
            var result = await write();
            return result.Ok ? Results.NoContent() : Invalid(result.Error!);
        }
        catch (DbException ex) when (ex.SqlState == IntegrityConstraintSqlState)
        {
            // A concurrent write took the (organisation, standard) pair between the pre-check and the
            // insert; the unique key rejected it. Report the same conflict the pre-check would have.
            return Conflict();
        }
        catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
        {
            // A lazily-opened connection can surface a store failure as a non-DbException (the web app
            // permits an empty connection string at startup). Map the same failures the read path does.
            return Unreachable();
        }
    }

    private static IResult Invalid(string detail) => Results.Problem(
        title: "Invalid compliance write",
        detail: detail,
        statusCode: StatusCodes.Status422UnprocessableEntity,
        type: "https://freeboard.io/problems/validation");

    private static IResult Conflict() => Results.Problem(
        title: "Conflicting compliance write",
        detail: "The write conflicts with an existing record. A scope already maps this organisation and standard.",
        statusCode: StatusCodes.Status409Conflict,
        type: "https://freeboard.io/problems/conflict");

    private static IResult Unreachable() => Results.Problem(
        title: "Compliance store unreachable",
        detail: "The compliance store could not be reached. Check the database connection.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
