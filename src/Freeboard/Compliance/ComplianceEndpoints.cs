using System.Security.Claims;
using Freeboard.Api;
using Freeboard.Persistence;
using Freeboard.Web;

namespace Freeboard.Compliance;

/// <summary>
/// Read-only HTTP endpoints serving the persisted compliance domain through
/// <see cref="IComplianceStore"/>. GET-only, so the read-only middleware does not
/// touch them, and behind the default authorization policy so an anonymous caller is 401'd
/// (any authenticated user may read; no admin role required). On an unreachable store the read
/// endpoints return RFC 7807 / HTTP 503; the status endpoint degrades to all-null counts with
/// HTTP 200.
/// </summary>
public static class ComplianceEndpoints
{
    public static void MapComplianceEndpoints(this WebApplication app)
    {
        var reads = app.MapGroup(ApiRoutes.ApiRoutePrefix).RequireAuthorization();

        reads.MapGet("/standards", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetStandardsAsync(ct);
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
                var rows = await store.GetRequirementsAsync(ct);
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
                var rows = await store.GetControlsAsync(ct);
                return Results.Ok(rows.Select(r => new { id = r.Id, title = r.Title, maps_to = r.MapsTo, evaluation = r.Evaluation }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/organisations", async (IComplianceStore store, IOrgAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetOrganisationsAsync(ct);
                var accessible = await access.AccessibleOrgIdsAsync(user, rows, ct);
                // Narrow to the accessible set, and null a parent id the caller cannot access so an
                // inaccessible organisation's existence is not disclosed (the selector treats a node
                // with a null parent as a root).
                return Results.Ok(rows.Where(r => accessible.Contains(r.Id)).Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    kind = r.Kind,
                    parent = r.Parent is not null && accessible.Contains(r.Parent) ? r.Parent : null,
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        // One unified scopes read, narrowed by subject readability. Each row carries its subject's resolved
        // type/owner/parent (server-side only); the response projects only the eight public fields, so no
        // subject type/state/parent/owner leaks. A subject that resolves to no asset row (or a retired
        // discovered asset), or whose resolving anchor is outside the accessible set, is omitted
        // (fail-closed), so neither the scope nor
        // its Out justification surfaces.
        reads.MapGet("/scopes", async (IComplianceStore store, IOrgAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetScopesAsync(ct);
                var organisations = await store.GetOrganisationsAsync(ct);
                var accessible = await access.AccessibleOrgIdsAsync(user, organisations, ct);
                var orgsById = organisations.ToDictionary(o => o.Id, StringComparer.Ordinal);
                return Results.Ok(rows.Where(r => SubjectReadable(r, accessible, orgsById)).Select(r => new
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

        // A vendor is visible only when its owner (a Company/Department asset) is in the caller's
        // accessible-org set; a vendor with a null or dangling owner is visible to no one (fail-closed).
        // This narrowing covers /vendors only; /collectors and /integration-connections still
        // expose a hidden vendor's id.
        reads.MapGet("/vendors", async (IComplianceStore store, IOrgAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var accessible = await access.AccessibleOrgIdsAsync(user, await store.GetOrganisationsAsync(ct), ct);
                var rows = await store.GetVendorsAsync(ct);
                return Results.Ok(rows.Where(r => r.Owner is not null && accessible.Contains(r.Owner))
                    .Select(r => new { id = r.Id, title = r.Title }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        // Collectors are org-independent reference data (no organisation dimension), so - like
        // /vendors - they are intentionally NOT narrowed by IOrgAccess: any authenticated user reads
        // every collector, including its config. Revisit if a later change adds an org dimension.
        // The quiz items carry no answer: the store returns an answer-free QuizItemView, so the correct
        // answer never appears in the JSON. `connection` is deliberately not projected - it is sourced
        // only to drive the startup token-resolvability warning.
        reads.MapGet("/collectors", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetCollectorsAsync(ct);
                return Results.Ok(rows.Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    control = r.Control,
                    vendor = r.Vendor,
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
        // /vendors and /collectors - they are intentionally NOT narrowed by IOrgAccess. token_resolvable
        // is composed at read time from the out-of-band token resolver; the token value never appears here.
        reads.MapGet("/integration-connections", async (IComplianceStore store, IIntegrationTokenResolver tokens, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetIntegrationConnectionsAsync(ct);
                return Results.Ok(rows.Select(r => new
                {
                    id = r.Id,
                    provider = r.Provider,
                    base_url = r.BaseUrl,
                    discovery_cadence = r.DiscoveryCadence,
                    vendor = r.Vendor,
                    token_resolvable = tokens.IsResolvable(r.Id),
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/statement-of-applicability/{standardId}",
            async (string standardId, IComplianceStore store, IOrgAccess access, ClaimsPrincipal user, CancellationToken ct) =>
            {
                try
                {
                    // Confirm the standard exists before projecting. Under opt-out an absent standard
                    // would otherwise resolve every organisation In by default, presenting a typo or
                    // deleted standard as applicable to all orgs instead of returning not found.
                    var standards = await store.GetStandardsAsync(ct);
                    if (!standards.Any(s => string.Equals(s.Id, standardId, StringComparison.Ordinal)))
                    {
                        return Results.NotFound();
                    }

                    var inputs = await store.GetStatementOfApplicabilityInputsAsync(ct);
                    // Resolve over the FULL tree first (so inherited dispositions survive), then filter
                    // the node list to the accessible subtree.
                    var accessible = await access.AccessibleOrgIdsAsync(user, inputs.Organisations, ct);
                    var nodes = StatementOfApplicability.Resolve(
                            inputs.Organisations, inputs.Scopes, inputs.Requirements, standardId)
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

    // The unified subject-readability rule with one branch per parent-anchored subject family, fail-closed.
    // An org subject is readable when it is in the accessible set; a vendor subject when its owner is; a
    // machine (or other parent-anchored) subject when its parent org's inclusive ancestry intersects the
    // accessible set (exact parity with the org rule, since the accessible set is a downward closure). A
    // subject unresolved by the subject-resolution predicate (no asset row, or a retired discovered asset)
    // is hidden.
    internal static bool SubjectReadable(
        ScopeRow row, IReadOnlySet<string> accessible, IReadOnlyDictionary<string, OrganisationRow> orgsById)
    {
        if (row.SubjectType is null)
        {
            return false;
        }

        if (string.Equals(row.SubjectSource, "discovered", StringComparison.Ordinal)
            && string.Equals(row.SubjectState, "Retired", StringComparison.Ordinal))
        {
            return false;
        }

        return row.SubjectType switch
        {
            "Company" or "Department" => accessible.Contains(row.Subject),
            "Vendor" => row.SubjectOwner is not null && accessible.Contains(row.SubjectOwner),
            _ => row.SubjectParent is not null
                && OrgAncestry.InclusiveAncestors(row.SubjectParent, orgsById).Any(accessible.Contains),
        };
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
