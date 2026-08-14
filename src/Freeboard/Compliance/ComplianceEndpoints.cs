using System.Globalization;
using System.Security.Claims;
using Freeboard.Api;
using Freeboard.Authz;
using Freeboard.Core.Assets;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.Extensions.Options;

namespace Freeboard.Compliance;

/// <summary>
/// Read-only HTTP endpoints serving the persisted compliance domain. GET-only, so the read-only
/// middleware does not touch them, and behind the default authorization policy so an anonymous caller
/// is 401'd (any authenticated user may read; no admin role required). On an unreachable store the read
/// endpoints return RFC 7807 / HTTP 503; the status endpoint degrades to all-null counts with
/// HTTP 200.
///
/// A narrowed endpoint takes its rows AND the asset list that narrows them from one
/// <see cref="AuthzRequestCache"/> snapshot naming exactly the sets its decision needs, so a sync
/// committing between two reads cannot pair one side's rows with the other side's owner edges. The
/// unnarrowed catalog and count reads go to <see cref="IComplianceStore"/> directly: they narrow
/// nothing, so they have nothing to pair and nothing for the request to share.
/// </summary>
public static class ComplianceEndpoints
{
    public static void MapComplianceEndpoints(this WebApplication app)
    {
        var reads = app.MapGroup(ApiRoutes.ApiRoutePrefix).RequireAuthorization();

        // The three catalog reads narrow nothing, so each names its one set and goes to the store
        // directly: there is no asset list to pair the rows with and nothing for the request to share.
        reads.MapGet("/standards", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = (await store.GetSnapshotAsync(ComplianceReadSet.Standards, ct)).Standards;
                return Results.Ok(rows.Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    version = r.Version,
                    authority = r.Authority,
                    publisher = r.Publisher,
                    source_url = r.SourceUrl,
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/requirements", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = (await store.GetSnapshotAsync(ComplianceReadSet.Requirements, ct)).Requirements;
                return Results.Ok(rows.Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    standard = r.Standard,
                    theme = r.Theme,
                    statement = r.Statement,
                    guidance = r.Guidance,
                    citation = new { label = r.CitationLabel, url = r.CitationUrl },
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/controls", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = (await store.GetSnapshotAsync(ComplianceReadSet.Controls, ct)).Controls;
                return Results.Ok(rows.Select(r => new { id = r.Id, title = r.Title, maps_to = r.MapsTo, evaluation = r.Evaluation }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/organisations", async (AuthzRequestCache cache, IAssetAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var assets = (await cache.GetSnapshotAsync(ComplianceReadSet.Assets, ct)).Assets;
                var accessible = await access.AccessibleAssetIdsAsync(user, assets, ct);
                var organisationIds = assets.Where(a => a.IsOrganisation).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
                // Narrow to the accessible set, and null a parent the caller cannot access so an
                // inaccessible organisation's existence is not disclosed (the selector treats a node
                // with a null parent as a root). The parent must also BE an organisation: `parent` on
                // this listing always names a row of the same listing, and an organisation parented onto
                // a readable machine would otherwise emit a machine id there.
                return Results.Ok(assets.Where(a => a.IsOrganisation && accessible.Contains(a.Id)).Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    kind = r.Type,
                    parent = r.Parent is not null && organisationIds.Contains(r.Parent) && accessible.Contains(r.Parent)
                        ? r.Parent
                        : null,
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        // One unified scopes read, narrowed by one test: the subject must be an asset the caller may
        // read. A subject that resolves to no asset row, to a retired discovered asset, or to one whose
        // anchoring edge lands outside the caller's set is omitted (fail-closed), so neither the scope
        // nor its Out justification surfaces. The scopes and the assets travel in one snapshot, so a
        // sync committing mid-read cannot admit a scope by owner edges the rows never had.
        reads.MapGet("/scopes", async (AuthzRequestCache cache, IAssetAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var snapshot = await cache.GetSnapshotAsync(ComplianceReadSet.Assets | ComplianceReadSet.Scopes, ct);
                var accessible = await access.AccessibleAssetIdsAsync(user, snapshot.Assets, ct);
                return Results.Ok(snapshot.Scopes.Where(r => accessible.Contains(r.Subject)).Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    subject = r.Subject,
                    standard = r.Standard,
                    requirement = r.Requirement,
                    control = r.Control,
                    disposition = r.Disposition,
                    justification = r.Justification,
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        // A vendor is visible only when it is in the caller's accessible asset set, which admits it
        // exactly when its owner resolves into the caller's organisation union; a vendor with a null or
        // dangling owner is visible to no one (fail-closed). The same set withholds a vendor id from
        // /collectors and /integration-connections, so no read surface discloses one.
        // The assets and the assurances come from one snapshot, so the owner edges that narrow the
        // response and the rows being narrowed cannot straddle a concurrent sync commit. The
        // status is derived here rather than left to the caller, so the warning window lives in one
        // process and this endpoint and the CLI cannot disagree about the state of one certification.
        reads.MapGet("/vendors", async (
            AuthzRequestCache cache,
            IAssetAccess access,
            TimeProvider clock,
            IOptions<AssuranceOptions> assurance,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                var snapshot = await cache.GetSnapshotAsync(
                    ComplianceReadSet.Assets | ComplianceReadSet.VendorAssurances, ct);
                var accessible = await access.AccessibleAssetIdsAsync(user, snapshot.Assets, ct);
                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                var warnWindowDays = assurance.Value.WarnWindowDays;
                var byVendor = snapshot.VendorAssurances
                    .GroupBy(a => a.VendorId, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

                return Results.Ok(snapshot.Assets.Where(a => a.Type is "Vendor" && accessible.Contains(a.Id))
                    .Select(r => new
                    {
                        id = r.Id,
                        title = r.Title,
                        tier = r.Tier,
                        data_classes = r.DataClasses,
                        assurances = (byVendor.TryGetValue(r.Id, out var rows) ? rows : [])
                            .Select(a => new
                            {
                                standard = a.StandardId,
                                expires = a.Expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                status = VendorAssurance.Evaluate(a.Expires, a.WarnDays ?? warnWindowDays, today).ToString(),
                            }),
                    }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        // Collectors are org-independent reference data (no organisation dimension), so the ROWS are not
        // narrowed: any authenticated user reads every collector, including its config. The `vendor` id
        // IS narrowed - it names a vendor asset, which the owner edge governs - and reads null when that
        // vendor is outside the caller's accessible asset set. Revisit the row rule if a later change
        // adds an org dimension. The quiz items carry no answer: the store returns an answer-free
        // QuizItemView, so the correct answer never appears in the JSON. `connection` is deliberately
        // not projected - it is sourced only to drive the startup token-resolvability warning.
        reads.MapGet("/collectors", async (AuthzRequestCache cache, IAssetAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var snapshot = await cache.GetSnapshotAsync(
                    ComplianceReadSet.Assets | ComplianceReadSet.Collectors, ct);
                var accessible = await access.AccessibleAssetIdsAsync(user, snapshot.Assets, ct);
                return Results.Ok(snapshot.Collectors.Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    control = r.Control,
                    vendor = r.Vendor is not null && accessible.Contains(r.Vendor) ? r.Vendor : null,
                    type = r.Type,
                    provider = r.Provider,
                    frequency = r.Frequency,
                    threshold = r.Threshold,
                    config = CollectorConfigPayload(r.Config),
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        // Integration-connections are org-independent reference data (no organisation dimension), so - like
        // /collectors - the ROWS are not narrowed and the `vendor` id is: it reads null when that vendor is
        // outside the caller's accessible asset set. token_resolvable is composed at read time from the
        // out-of-band token resolver; the token value never appears here.
        reads.MapGet("/integration-connections", async (AuthzRequestCache cache, IIntegrationTokenResolver tokens, IAssetAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var snapshot = await cache.GetSnapshotAsync(
                    ComplianceReadSet.Assets | ComplianceReadSet.IntegrationConnections, ct);
                var accessible = await access.AccessibleAssetIdsAsync(user, snapshot.Assets, ct);
                return Results.Ok(snapshot.IntegrationConnections.Select(r => new
                {
                    id = r.Id,
                    provider = r.Provider,
                    base_url = r.BaseUrl,
                    discovery_cadence = r.DiscoveryCadence,
                    vendor = r.Vendor is not null && accessible.Contains(r.Vendor) ? r.Vendor : null,
                    token_resolvable = tokens.IsResolvable(r.Id),
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/statement-of-applicability/{standardId}",
            async (string standardId, AuthzRequestCache cache, IComplianceStore store, IAssetAccess access, ClaimsPrincipal user, CancellationToken ct) =>
            {
                try
                {
                    // Confirm the standard exists before projecting. Under opt-out an absent standard
                    // would otherwise resolve every organisation In by default, presenting a typo or
                    // deleted standard as applicable to all orgs instead of returning not found.
                    // A catalog read, kept outside the projection snapshot: it decides not-found rather
                    // than visibility, so a standard appearing or vanishing mid-read discloses nothing.
                    var standards = (await store.GetSnapshotAsync(ComplianceReadSet.Standards, ct)).Standards;
                    if (!standards.Any(s => string.Equals(s.Id, standardId, StringComparison.Ordinal)))
                    {
                        return Results.NotFound();
                    }

                    var snapshot = await cache.GetSnapshotAsync(
                        ComplianceReadSet.Assets | ComplianceReadSet.Scopes | ComplianceReadSet.Requirements, ct);
                    // Resolve over the FULL tree first (so inherited dispositions survive), then filter
                    // the node list to the accessible subtree.
                    var accessible = await access.AccessibleAssetIdsAsync(user, snapshot.Assets, ct);
                    var nodes = StatementOfApplicability.Resolve(
                            snapshot.Assets, snapshot.Scopes, snapshot.Requirements, standardId)
                        .Where(n => accessible.Contains(n.Id))
                        .ToList();
                    return Results.Ok(new
                    {
                        standard = standardId,
                        nodes = nodes.Select(n => new
                        {
                            id = n.Id,
                            title = n.Title,
                            kind = n.Kind,
                            parent = n.Parent is not null && accessible.Contains(n.Parent) ? n.Parent : null,
                            disposition = n.Disposition,
                            resolution = n.Resolution.ToWireValue(),
                            requirements = n.Requirements.Select(r => new
                            {
                                requirement = r.Requirement,
                                disposition = r.Disposition,
                                resolution = r.Resolution.ToWireValue(),
                            }),
                        }),
                    });
                }
                catch (Exception ex) when (IsStoreFailure(ex))
                {
                    return Unreachable();
                }
            });

        reads.MapGet("/compliance/status", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var counts = await store.GetCountsAsync(ct);
                return Results.Ok(new
                {
                    persisted = new
                    {
                        standards = (int?)counts.Standards,
                        controls = (int?)counts.Controls,
                        requirements = (int?)counts.Requirements,
                        organisations = (int?)counts.Organisations,
                        scopes = (int?)counts.Scopes,
                        vendors = (int?)counts.Vendors,
                        collectors = (int?)counts.Collectors,
                    },
                });
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                // Degrade to all-null counts; the app stays up.
                return Results.Ok(new
                {
                    persisted = new
                    {
                        standards = (int?)null,
                        controls = (int?)null,
                        requirements = (int?)null,
                        organisations = (int?)null,
                        scopes = (int?)null,
                        vendors = (int?)null,
                        collectors = (int?)null,
                    },
                });
            }
        });
    }

    // A config key is written only when its member carries a value, so a collector serializes exactly
    // the keys its (type, provider) schema can register: `{}` for script and agent, no `checks` on an
    // attestation, no attestation key on an integration collector. Serializing the fixed five-member
    // view directly would emit all five on every collector and contradict that. The omission stops at
    // `config`: the top-level `vendor`, `provider`, and `threshold` stay explicit nulls, because those
    // are members every collector has and merely leaves unset.
    private static Dictionary<string, object> CollectorConfigPayload(CollectorConfigView config)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(config.Body))
        {
            payload["body"] = config.Body;
        }

        if (config.Fields.Count > 0)
        {
            payload["fields"] = config.Fields
                .Select(f => new { id = f.Id, label = f.Label, type = f.Type, options = f.Options })
                .ToList();
        }

        if (config.PassMark is not null)
        {
            payload["pass_mark"] = config.PassMark;
        }

        if (config.Quiz.Count > 0)
        {
            payload["quiz"] = config.Quiz
                .Select(q => new { id = q.Id, prompt = q.Prompt, options = q.Options })
                .ToList();
        }

        if (config.Checks.Count > 0)
        {
            payload["checks"] = config.Checks
                .Select(c => new { source_key = c.SourceKey, name = c.Name, severity = c.Severity })
                .ToList();
        }

        return payload;
    }

    private static IResult Unreachable() => Results.Problem(
        title: "Compliance store unreachable",
        detail: "The compliance store could not be reached. Check the database connection.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    internal static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException
            or InvalidOperationException
            or TimeoutException;
}
