using System.Globalization;
using Freeboard.Core.Assets;

namespace Freeboard.Core.GitOps;

/// <summary>
/// Validates a loaded <see cref="GitOpsConfig"/>. Collects every error as a
/// <see cref="Diagnostic"/>; never throws and never writes output. Owns: required
/// fields, apiVersion value, unique id per kind, reference resolution, the asset set (type/source
/// tokens, mutually-exclusive parent/owner edges with their target/carrier-type rules, and dangling
/// edges, parent cycles, and missing read anchors as non-blocking warnings), the scope mapping (resolvable references,
/// disposition enum, unique organisation/standard pair), the requirement-scope mapping
/// (resolvable references, disposition enum, unique organisation/requirement pair), and the
/// vendor-scope mapping (exactly-one target, resolvable references, disposition enum, unique
/// vendor/target pair, justification required when Out), and the evidence-collectors (resolvable
/// control/vendor references, type/frequency/threshold checks, the control evaluation rule required once
/// a control has an attached collector, the type-conditional connection/checks rules, and each tracked
/// check's shape and severity token), the integration-connections (required fields, closed provider
/// token, absolute base_url, discovery_cadence token, optional vendor reference, and a
/// configuration-key-safe id: no ':' or '__', no case-insensitive collision), and the
/// attestation-templates (resolvable control reference, type token, field/quiz shape, pass_mark range,
/// and the training-vs-manual conditional rules). Does NOT re-check kind (the loader owns kind-routing).
/// </summary>
public static class ConfigValidator
{
    /// <summary>Closed token set for a control's evaluation rule (case-sensitive).</summary>
    private static readonly HashSet<string> EvaluationTokens = new(StringComparer.Ordinal) { "all", "any", "manual" };

    /// <summary>Closed token set for an evidence-collector's type (case-sensitive).</summary>
    private static readonly HashSet<string> CollectorTypeTokens = new(StringComparer.Ordinal)
    {
        "integration", "script", "manual-attestation", "training-attestation", "agent",
    };

    /// <summary>Closed token set for an attestation-template's type (case-sensitive).</summary>
    private static readonly HashSet<string> AttestationTypeTokens = new(StringComparer.Ordinal) { "manual", "training" };

    /// <summary>Closed token set for an attestation field's type (case-sensitive).</summary>
    private static readonly HashSet<string> FieldTypeTokens = new(StringComparer.Ordinal)
    {
        "boolean", "single-choice", "short-text",
    };

    /// <summary>Closed token set for a tracked check's severity (case-sensitive), matching evidence_checks.severity.</summary>
    private static readonly HashSet<string> CheckSeverityTokens = new(StringComparer.Ordinal) { "Hard", "Soft" };

    /// <summary>
    /// Loads <paramref name="directory"/> then validates it, returning the model and
    /// the combined loader plus validator diagnostics.
    /// </summary>
    public static ConfigResult LoadAndValidate(string directory)
    {
        var loaded = ConfigLoader.Load(directory);
        var diagnostics = new List<Diagnostic>(loaded.Diagnostics);
        diagnostics.AddRange(Validate(loaded.Config));
        return loaded with { Diagnostics = diagnostics };
    }

    /// <summary>
    /// Validates an already-loaded config and returns all validation diagnostics.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Validate(GitOpsConfig config)
    {
        var diagnostics = new List<Diagnostic>();

        // Fixed phase order: standards first (produces the standard id set), then requirements
        // (consumes it and produces the requirement id set), then controls (resolves maps_to
        // against the requirement id set), then organisations and scopes.
        var standardIds = ValidateStandards(config, diagnostics);
        var requirementIds = ValidateRequirements(config, standardIds, diagnostics);
        var controlIds = ValidateControls(config, requirementIds, diagnostics);
        // Assets produce the typed id subsets the reference phases consume: organisation refs resolve
        // against Company/Department asset ids, vendor refs against Vendor asset ids.
        var assets = ValidateAssets(config, diagnostics);
        ValidateScopes(config, assets.OrganisationIds, standardIds, diagnostics);
        ValidateRequirementScopes(config, assets.OrganisationIds, requirementIds, diagnostics);
        var vendorIds = assets.VendorIds;
        ValidateVendorScopes(config, vendorIds, requirementIds, controlIds, diagnostics);
        // Integration-connections consume vendor ids (for the optional vendor reference) and produce the
        // connection id set the evidence-collectors then resolve their connection reference against.
        var connectionIds = ValidateIntegrationConnections(config, vendorIds, diagnostics);
        ValidateEvidenceCollectors(config, controlIds, vendorIds, connectionIds, diagnostics);
        ValidateAttestationTemplates(config, controlIds, diagnostics);

        return diagnostics;
    }

    private static HashSet<string> ValidateStandards(GitOpsConfig config, List<Diagnostic> diagnostics)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var standard in config.Standards)
        {
            CheckApiVersion(standard.ApiVersion, GitOpsSchema.KindStandard, standard.Id, diagnostics);
            CheckRequired(standard.Id, GitOpsSchema.KindStandard, "id", standard.Title, diagnostics);
            CheckRequired(standard.Title, GitOpsSchema.KindStandard, "title", standard.Id, diagnostics);
            CheckRequired(standard.Version, GitOpsSchema.KindStandard, "version", standard.Id, diagnostics);
            CheckRequired(standard.Authority, GitOpsSchema.KindStandard, "authority", standard.Id, diagnostics);

            // source_url is optional (blank means absent); the URL-format check runs only when present.
            if (!string.IsNullOrWhiteSpace(standard.SourceUrl) && !IsAbsoluteHttpUri(standard.SourceUrl))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindStandard} '{Describe(standard.Id)}' has malformed source_url "
                        + $"'{standard.SourceUrl}'. Expected an absolute http or https URL.",
                });
            }

            if (!string.IsNullOrEmpty(standard.Id))
            {
                if (!seen.Add(standard.Id))
                {
                    diagnostics.Add(Dup(GitOpsSchema.KindStandard, standard.Id));
                }

                ids.Add(standard.Id);
            }
        }

        return ids;
    }

    private static HashSet<string> ValidateRequirements(
        GitOpsConfig config,
        HashSet<string> standardIds,
        List<Diagnostic> diagnostics)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var requirement in config.Requirements)
        {
            CheckApiVersion(requirement.ApiVersion, GitOpsSchema.KindRequirement, requirement.Id, diagnostics);
            CheckRequired(requirement.Id, GitOpsSchema.KindRequirement, "id", requirement.Title, diagnostics);
            CheckRequired(requirement.Title, GitOpsSchema.KindRequirement, "title", requirement.Id, diagnostics);
            CheckRequired(requirement.Standard, GitOpsSchema.KindRequirement, "standard", requirement.Id, diagnostics);
            CheckRequired(requirement.Theme, GitOpsSchema.KindRequirement, "theme", requirement.Id, diagnostics);
            CheckRequired(requirement.Statement, GitOpsSchema.KindRequirement, "statement", requirement.Id, diagnostics);
            CheckRequired(requirement.CitationLabel, GitOpsSchema.KindRequirement, "citation_label", requirement.Id, diagnostics);
            CheckRequired(requirement.CitationUrl, GitOpsSchema.KindRequirement, "citation_url", requirement.Id, diagnostics);

            if (!string.IsNullOrWhiteSpace(requirement.CitationUrl) && !IsAbsoluteHttpUri(requirement.CitationUrl))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindRequirement} '{Describe(requirement.Id)}' has malformed citation_url "
                        + $"'{requirement.CitationUrl}'. Expected an absolute http or https URL.",
                });
            }

            if (!string.IsNullOrEmpty(requirement.Standard) && !standardIds.Contains(requirement.Standard))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindRequirement} '{Describe(requirement.Id)}' references unknown Standard id "
                        + $"'{requirement.Standard}'.",
                });
            }

            if (!string.IsNullOrEmpty(requirement.Id))
            {
                if (!seen.Add(requirement.Id))
                {
                    diagnostics.Add(Dup(GitOpsSchema.KindRequirement, requirement.Id));
                }

                ids.Add(requirement.Id);
            }
        }

        return ids;
    }

    private static HashSet<string> ValidateControls(
        GitOpsConfig config,
        HashSet<string> requirementIds,
        List<Diagnostic> diagnostics)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var control in config.Controls)
        {
            CheckApiVersion(control.ApiVersion, GitOpsSchema.KindControl, control.Id, diagnostics);
            CheckRequired(control.Id, GitOpsSchema.KindControl, "id", control.Title, diagnostics);
            CheckRequired(control.Title, GitOpsSchema.KindControl, "title", control.Id, diagnostics);

            if (control.MapsTo.Count == 0)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindControl} '{Describe(control.Id)}' is missing required field 'maps_to'.",
                });
            }

            foreach (var requirementId in control.MapsTo.Where(requirementId => !requirementIds.Contains(requirementId)))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindControl} '{Describe(control.Id)}' maps_to unknown Requirement id '{requirementId}'.",
                });
            }

            CheckNoDuplicateRefs(
                control.MapsTo, GitOpsSchema.KindControl, control.Id, "maps_to", "Requirement", diagnostics);

            // The evaluation rule is optional here (the required-when-collectors check runs in the
            // collector phase, once the attached-control set is known); when present it must be a token.
            if (!string.IsNullOrEmpty(control.Evaluation) && !EvaluationTokens.Contains(control.Evaluation))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindControl} '{Describe(control.Id)}' has unknown evaluation "
                        + $"'{control.Evaluation}'. Expected 'all', 'any', or 'manual'.",
                });
            }

            if (!string.IsNullOrEmpty(control.Id))
            {
                if (!seen.Add(control.Id))
                {
                    diagnostics.Add(Dup(GitOpsSchema.KindControl, control.Id));
                }

                ids.Add(control.Id);
            }
        }

        return ids;
    }

    /// <summary>The typed id subsets a validated asset set exposes to the reference phases.</summary>
    private sealed record AssetIdSets(HashSet<string> OrganisationIds, HashSet<string> VendorIds);

    private static AssetIdSets ValidateAssets(GitOpsConfig config, List<Diagnostic> diagnostics)
    {
        var allIds = new HashSet<string>(StringComparer.Ordinal);
        var organisationIds = new HashSet<string>(StringComparer.Ordinal);
        var vendorIds = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // First-occurrence type per id, for resolving parent/owner target-type rules across rows.
        var typeById = new Dictionary<string, AssetKind>(StringComparer.Ordinal);

        foreach (var asset in config.Assets)
        {
            CheckApiVersion(asset.ApiVersion, GitOpsSchema.KindAsset, asset.Id, diagnostics);
            CheckRequired(asset.Id, GitOpsSchema.KindAsset, "id", asset.Title, diagnostics);
            CheckRequired(asset.Title, GitOpsSchema.KindAsset, "title", asset.Id, diagnostics);
            CheckRequired(asset.Type, GitOpsSchema.KindAsset, "type", asset.Id, diagnostics);
            CheckRequired(asset.Source, GitOpsSchema.KindAsset, "source", asset.Id, diagnostics);

            var typeParsed = TryParseAssetType(asset.Type, out var type);
            if (!string.IsNullOrEmpty(asset.Type) && !typeParsed)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' has unknown type '{asset.Type}'. "
                        + $"Expected '{nameof(AssetKind.Company)}', '{nameof(AssetKind.Department)}', "
                        + $"'{nameof(AssetKind.Machine)}', or '{nameof(AssetKind.Vendor)}'.",
                });
            }

            ValidateAssetSource(asset, diagnostics);

            // parent and owner are mutually exclusive: parent is containment, owner is accountability.
            // A whitespace-only value is absent, matching the spec and the import path's null-if-blank.
            var hasParent = !string.IsNullOrWhiteSpace(asset.Parent);
            var hasOwner = !string.IsNullOrWhiteSpace(asset.Owner);
            if (hasParent && hasOwner)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' sets both 'parent' and 'owner'; "
                        + "an asset has at most one edge.",
                });
            }

            // Carrier-type rules: parent lives on a contained asset (Company/Department/Machine), owner
            // only on a Vendor. These compare the carrier's own type, so they run here in the first pass.
            if (typeParsed && hasParent && type == AssetKind.Vendor)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' is a Vendor and cannot set 'parent'; "
                        + "a vendor uses 'owner'.",
                });
            }

            if (typeParsed && hasOwner && type != AssetKind.Vendor)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' sets 'owner' but is not a Vendor; "
                        + "only a vendor has an owner.",
                });
            }

            if (!string.IsNullOrEmpty(asset.Id))
            {
                if (!seen.Add(asset.Id))
                {
                    diagnostics.Add(Dup(GitOpsSchema.KindAsset, asset.Id));
                }

                allIds.Add(asset.Id);
                if (typeParsed && !typeById.ContainsKey(asset.Id))
                {
                    typeById[asset.Id] = type;
                    if (type is AssetKind.Company or AssetKind.Department)
                    {
                        organisationIds.Add(asset.Id);
                    }
                    else if (type == AssetKind.Vendor)
                    {
                        vendorIds.Add(asset.Id);
                    }
                }
            }
        }

        ValidateAssetEdges(config, allIds, typeById, diagnostics);
        return new AssetIdSets(organisationIds, vendorIds);
    }

    private static void ValidateAssetSource(Asset asset, List<Diagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(asset.Source))
        {
            return;
        }

        if (!TryParseAssetSource(asset.Source, out var source))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' has unknown source '{asset.Source}'. "
                    + "Expected 'declared'.",
            });
            return;
        }

        // A discovered asset is owned by ingest, never authored: reject 'source: discovered' in config.
        if (source == AssetSource.Discovered)
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' has source 'discovered', which cannot be "
                    + "authored in config; discovered assets come from ingest.",
            });
        }
    }

    private static void ValidateAssetEdges(
        GitOpsConfig config,
        HashSet<string> allIds,
        IReadOnlyDictionary<string, AssetKind> typeById,
        List<Diagnostic> diagnostics)
    {
        foreach (var asset in config.Assets)
        {
            CheckEdgeTarget(asset.Id, asset.Parent, "parent", allIds, typeById, diagnostics);
            CheckEdgeTarget(asset.Id, asset.Owner, "owner", allIds, typeById, diagnostics);
        }

        WarnAssetParentCycles(config, allIds, diagnostics);
        WarnMissingRequiredEdges(config, typeById, diagnostics);
    }

    // A parent/owner edge target must be a Company/Department asset. A dangling edge (naming an id no
    // asset defines) is tolerated as a Warning: a discovered child can name a declared parent a later
    // sync removes, and one uncoordinated writer must not wedge the whole config.
    private static void CheckEdgeTarget(
        string id,
        string target,
        string edge,
        HashSet<string> allIds,
        IReadOnlyDictionary<string, AssetKind> typeById,
        List<Diagnostic> diagnostics)
    {
        // A whitespace-only edge is absent, matching the spec and the import path's null-if-blank.
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        if (!allIds.Contains(target))
        {
            diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = $"{GitOpsSchema.KindAsset} '{Describe(id)}' has unknown {edge} '{target}'.",
            });
            return;
        }

        if (typeById.TryGetValue(target, out var targetType)
            && targetType is not (AssetKind.Company or AssetKind.Department))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindAsset} '{Describe(id)}' {edge} '{target}' must be a Company or "
                    + "Department asset.",
            });
        }
    }

    // A parent cycle among declared assets is tolerated as a Warning: the read-access and inheritance
    // walks carry a visited-set cycle guard, so a cycle degrades gracefully rather than crashing.
    private static void WarnAssetParentCycles(GitOpsConfig config, HashSet<string> allIds, List<Diagnostic> diagnostics)
    {
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var asset in config.Assets)
        {
            if (string.IsNullOrEmpty(asset.Id) || parentOf.ContainsKey(asset.Id))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(asset.Parent) && allIds.Contains(asset.Parent))
            {
                parentOf[asset.Id] = asset.Parent;
            }
        }

        foreach (var start in parentOf.Keys)
        {
            var walked = new HashSet<string>(StringComparer.Ordinal);
            var node = start;
            while (parentOf.TryGetValue(node, out var parent))
            {
                if (!walked.Add(node))
                {
                    break;
                }

                if (parent == start)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Message = $"{GitOpsSchema.KindAsset} '{Describe(start)}' is part of a parent cycle.",
                    });
                    break;
                }

                node = parent;
            }
        }
    }

    // A required read anchor missing entirely is a Warning, not an Error (a stronger version of the
    // dangling-edge tolerance): a Vendor with no owner and a Machine with no parent are visible to no
    // caller under the fail-closed read model, but blocking sync on it would wedge on one writer. Not
    // emitted for a parent-less Company/Department, which is a legitimate root.
    private static void WarnMissingRequiredEdges(
        GitOpsConfig config, IReadOnlyDictionary<string, AssetKind> typeById, List<Diagnostic> diagnostics)
    {
        foreach (var asset in config.Assets)
        {
            if (string.IsNullOrEmpty(asset.Id) || !typeById.TryGetValue(asset.Id, out var type))
            {
                continue;
            }

            if (type == AssetKind.Vendor && string.IsNullOrWhiteSpace(asset.Owner))
            {
                diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' is a Vendor with no owner; it is "
                        + "visible to no caller until an owner is set.",
                });
            }
            else if (type == AssetKind.Machine && string.IsNullOrWhiteSpace(asset.Parent))
            {
                diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' is a Machine with no parent; it is "
                        + "visible to no caller until a parent is set.",
                });
            }
        }
    }

    private static void ValidateScopes(
        GitOpsConfig config,
        HashSet<string> organisationIds,
        HashSet<string> standardIds,
        List<Diagnostic> diagnostics)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenPairs = new HashSet<(string, string)>();

        foreach (var scope in config.Scopes)
        {
            CheckApiVersion(scope.ApiVersion, GitOpsSchema.KindScope, scope.Id, diagnostics);
            CheckRequired(scope.Id, GitOpsSchema.KindScope, "id", scope.Title, diagnostics);
            CheckRequired(scope.Title, GitOpsSchema.KindScope, "title", scope.Id, diagnostics);
            CheckRequired(scope.Organisation, GitOpsSchema.KindScope, "organisation", scope.Id, diagnostics);
            CheckRequired(scope.Standard, GitOpsSchema.KindScope, "standard", scope.Id, diagnostics);
            CheckRequired(scope.Disposition, GitOpsSchema.KindScope, "disposition", scope.Id, diagnostics);

            if (!string.IsNullOrEmpty(scope.Organisation) && !organisationIds.Contains(scope.Organisation))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' references unknown Organisation id "
                        + $"'{scope.Organisation}'.",
                });
            }

            if (!string.IsNullOrEmpty(scope.Standard) && !standardIds.Contains(scope.Standard))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' references unknown Standard id "
                        + $"'{scope.Standard}'.",
                });
            }

            if (!string.IsNullOrEmpty(scope.Disposition) && !TryParseDisposition(scope.Disposition, out _))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' has unknown disposition "
                        + $"'{scope.Disposition}'. Expected '{nameof(ScopeDisposition.In)}' or "
                        + $"'{nameof(ScopeDisposition.Out)}'.",
                });
            }

            if (!string.IsNullOrEmpty(scope.Id) && !seenIds.Add(scope.Id))
            {
                diagnostics.Add(Dup(GitOpsSchema.KindScope, scope.Id));
            }

            if (!string.IsNullOrEmpty(scope.Organisation)
                && !string.IsNullOrEmpty(scope.Standard)
                && !seenPairs.Add((scope.Organisation, scope.Standard)))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} maps organisation '{scope.Organisation}' to standard "
                        + $"'{scope.Standard}' more than once.",
                });
            }
        }
    }

    private static void ValidateRequirementScopes(
        GitOpsConfig config,
        HashSet<string> organisationIds,
        HashSet<string> requirementIds,
        List<Diagnostic> diagnostics)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenPairs = new HashSet<(string, string)>();

        foreach (var requirementScope in config.RequirementScopes)
        {
            CheckApiVersion(requirementScope.ApiVersion, GitOpsSchema.KindRequirementScope, requirementScope.Id, diagnostics);
            CheckRequired(requirementScope.Id, GitOpsSchema.KindRequirementScope, "id", requirementScope.Title, diagnostics);
            CheckRequired(requirementScope.Title, GitOpsSchema.KindRequirementScope, "title", requirementScope.Id, diagnostics);
            CheckRequired(requirementScope.Organisation, GitOpsSchema.KindRequirementScope, "organisation", requirementScope.Id, diagnostics);
            CheckRequired(requirementScope.Requirement, GitOpsSchema.KindRequirementScope, "requirement", requirementScope.Id, diagnostics);
            CheckRequired(requirementScope.Disposition, GitOpsSchema.KindRequirementScope, "disposition", requirementScope.Id, diagnostics);

            if (!string.IsNullOrEmpty(requirementScope.Organisation) && !organisationIds.Contains(requirementScope.Organisation))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindRequirementScope} '{Describe(requirementScope.Id)}' references unknown Organisation id "
                        + $"'{requirementScope.Organisation}'.",
                });
            }

            if (!string.IsNullOrEmpty(requirementScope.Requirement) && !requirementIds.Contains(requirementScope.Requirement))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindRequirementScope} '{Describe(requirementScope.Id)}' references unknown Requirement id "
                        + $"'{requirementScope.Requirement}'.",
                });
            }

            if (!string.IsNullOrEmpty(requirementScope.Disposition) && !TryParseDisposition(requirementScope.Disposition, out _))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindRequirementScope} '{Describe(requirementScope.Id)}' has unknown disposition "
                        + $"'{requirementScope.Disposition}'. Expected '{nameof(ScopeDisposition.In)}' or "
                        + $"'{nameof(ScopeDisposition.Out)}'.",
                });
            }

            if (!string.IsNullOrEmpty(requirementScope.Id) && !seenIds.Add(requirementScope.Id))
            {
                diagnostics.Add(Dup(GitOpsSchema.KindRequirementScope, requirementScope.Id));
            }

            if (!string.IsNullOrEmpty(requirementScope.Organisation)
                && !string.IsNullOrEmpty(requirementScope.Requirement)
                && !seenPairs.Add((requirementScope.Organisation, requirementScope.Requirement)))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindRequirementScope} maps organisation '{requirementScope.Organisation}' to requirement "
                        + $"'{requirementScope.Requirement}' more than once.",
                });
            }
        }
    }

    private static void ValidateVendorScopes(
        GitOpsConfig config,
        HashSet<string> vendorIds,
        HashSet<string> requirementIds,
        HashSet<string> controlIds,
        List<Diagnostic> diagnostics)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenRequirementPairs = new HashSet<(string, string)>();
        var seenControlPairs = new HashSet<(string, string)>();

        foreach (var vendorScope in config.VendorScopes)
        {
            CheckApiVersion(vendorScope.ApiVersion, GitOpsSchema.KindVendorScope, vendorScope.Id, diagnostics);
            CheckRequired(vendorScope.Id, GitOpsSchema.KindVendorScope, "id", vendorScope.Title, diagnostics);
            CheckRequired(vendorScope.Title, GitOpsSchema.KindVendorScope, "title", vendorScope.Id, diagnostics);
            CheckRequired(vendorScope.Vendor, GitOpsSchema.KindVendorScope, "vendor", vendorScope.Id, diagnostics);
            CheckRequired(vendorScope.Disposition, GitOpsSchema.KindVendorScope, "disposition", vendorScope.Id, diagnostics);

            var hasRequirement = !string.IsNullOrWhiteSpace(vendorScope.Requirement);
            var hasControl = !string.IsNullOrWhiteSpace(vendorScope.Control);
            if (hasRequirement == hasControl)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindVendorScope} '{Describe(vendorScope.Id)}' must name exactly one of "
                        + "'requirement' or 'control', not both and not neither.",
                });
            }

            if (!string.IsNullOrEmpty(vendorScope.Vendor) && !vendorIds.Contains(vendorScope.Vendor))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindVendorScope} '{Describe(vendorScope.Id)}' references unknown Vendor id "
                        + $"'{vendorScope.Vendor}'.",
                });
            }

            if (hasRequirement && !requirementIds.Contains(vendorScope.Requirement))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindVendorScope} '{Describe(vendorScope.Id)}' references unknown Requirement id "
                        + $"'{vendorScope.Requirement}'.",
                });
            }

            if (hasControl && !controlIds.Contains(vendorScope.Control))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindVendorScope} '{Describe(vendorScope.Id)}' references unknown Control id "
                        + $"'{vendorScope.Control}'.",
                });
            }

            var dispositionParsed = TryParseDisposition(vendorScope.Disposition, out var disposition);
            if (!string.IsNullOrEmpty(vendorScope.Disposition) && !dispositionParsed)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindVendorScope} '{Describe(vendorScope.Id)}' has unknown disposition "
                        + $"'{vendorScope.Disposition}'. Expected '{nameof(ScopeDisposition.In)}' or "
                        + $"'{nameof(ScopeDisposition.Out)}'.",
                });
            }

            // The one net-new rule: an Out exception must carry its rationale. An In scope may omit it.
            if (dispositionParsed && disposition == ScopeDisposition.Out
                && string.IsNullOrWhiteSpace(vendorScope.Justification))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindVendorScope} '{Describe(vendorScope.Id)}' has disposition 'Out' but is "
                        + "missing required field 'justification'.",
                });
            }

            if (!string.IsNullOrEmpty(vendorScope.Id) && !seenIds.Add(vendorScope.Id))
            {
                diagnostics.Add(Dup(GitOpsSchema.KindVendorScope, vendorScope.Id));
            }

            if (!string.IsNullOrEmpty(vendorScope.Vendor) && hasRequirement
                && !seenRequirementPairs.Add((vendorScope.Vendor, vendorScope.Requirement)))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindVendorScope} maps vendor '{vendorScope.Vendor}' to requirement "
                        + $"'{vendorScope.Requirement}' more than once.",
                });
            }

            if (!string.IsNullOrEmpty(vendorScope.Vendor) && hasControl
                && !seenControlPairs.Add((vendorScope.Vendor, vendorScope.Control)))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindVendorScope} maps vendor '{vendorScope.Vendor}' to control "
                        + $"'{vendorScope.Control}' more than once.",
                });
            }
        }
    }

    private static HashSet<string> ValidateIntegrationConnections(
        GitOpsConfig config,
        HashSet<string> vendorIds,
        List<Diagnostic> diagnostics)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // The id resolves a configuration key (Freeboard:Integrations:<id>:ApiToken) and .NET config keys
        // are case-insensitive, so two ids differing only in case would resolve the same token slot. This
        // set catches that collision before it reaches the store.
        var seenCaseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var connection in config.IntegrationConnections)
        {
            CheckApiVersion(connection.ApiVersion, GitOpsSchema.KindIntegrationConnection, connection.Id, diagnostics);
            CheckRequired(connection.Id, GitOpsSchema.KindIntegrationConnection, "id", connection.Title, diagnostics);
            CheckRequired(connection.Title, GitOpsSchema.KindIntegrationConnection, "title", connection.Id, diagnostics);
            CheckRequired(connection.Provider, GitOpsSchema.KindIntegrationConnection, "provider", connection.Id, diagnostics);
            CheckRequired(connection.BaseUrl, GitOpsSchema.KindIntegrationConnection, "base_url", connection.Id, diagnostics);
            CheckRequired(connection.DiscoveryCadence, GitOpsSchema.KindIntegrationConnection, "discovery_cadence", connection.Id, diagnostics);

            if (!string.IsNullOrEmpty(connection.Provider) && !IntegrationProvider.Tokens.Contains(connection.Provider))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindIntegrationConnection} '{Describe(connection.Id)}' has unknown provider "
                        + $"'{connection.Provider}'. Expected one of: fleet.",
                });
            }

            if (!string.IsNullOrWhiteSpace(connection.BaseUrl) && !IsAbsoluteHttpUri(connection.BaseUrl))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindIntegrationConnection} '{Describe(connection.Id)}' has malformed base_url "
                        + $"'{connection.BaseUrl}'. Expected an absolute http or https URL.",
                });
            }

            if (!string.IsNullOrEmpty(connection.DiscoveryCadence)
                && !EvidenceCollectorFrequency.Tokens.Contains(connection.DiscoveryCadence))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindIntegrationConnection} '{Describe(connection.Id)}' has unknown discovery_cadence "
                        + $"'{connection.DiscoveryCadence}'. Expected one of: continuous, daily, weekly, monthly, quarterly, annual.",
                });
            }

            if (!string.IsNullOrEmpty(connection.Vendor) && !vendorIds.Contains(connection.Vendor))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindIntegrationConnection} '{Describe(connection.Id)}' references unknown Vendor id "
                        + $"'{connection.Vendor}'.",
                });
            }

            if (!string.IsNullOrEmpty(connection.Id))
            {
                // The id is interpolated into the token config key Freeboard:Integrations:<id>:ApiToken. .NET
                // config keys are ':'-delimited and the environment-variable provider maps '__' to ':', so an
                // id containing either would address the wrong or an ambiguous token slot.
                if (connection.Id.Contains(':') || connection.Id.Contains("__", StringComparison.Ordinal))
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindIntegrationConnection} '{Describe(connection.Id)}' has an id containing "
                            + "':' or '__', which is not a safe configuration-key segment for out-of-band token resolution.",
                    });
                }

                if (!seen.Add(connection.Id))
                {
                    diagnostics.Add(Dup(GitOpsSchema.KindIntegrationConnection, connection.Id));
                }
                else if (!seenCaseInsensitive.Add(connection.Id))
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindIntegrationConnection} '{Describe(connection.Id)}' collides case-insensitively "
                            + "with another connection id; the token resolves from a case-insensitive configuration key.",
                    });
                }

                ids.Add(connection.Id);
            }
        }

        return ids;
    }

    private static void ValidateEvidenceCollectors(
        GitOpsConfig config,
        HashSet<string> controlIds,
        HashSet<string> vendorIds,
        HashSet<string> connectionIds,
        List<Diagnostic> diagnostics)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var controlsWithCollector = new HashSet<string>(StringComparer.Ordinal);

        foreach (var collector in config.EvidenceCollectors)
        {
            CheckApiVersion(collector.ApiVersion, GitOpsSchema.KindEvidenceCollector, collector.Id, diagnostics);
            CheckRequired(collector.Id, GitOpsSchema.KindEvidenceCollector, "id", collector.Title, diagnostics);
            CheckRequired(collector.Title, GitOpsSchema.KindEvidenceCollector, "title", collector.Id, diagnostics);
            CheckRequired(collector.Control, GitOpsSchema.KindEvidenceCollector, "control", collector.Id, diagnostics);
            CheckRequired(collector.Type, GitOpsSchema.KindEvidenceCollector, "type", collector.Id, diagnostics);
            CheckRequired(collector.Frequency, GitOpsSchema.KindEvidenceCollector, "frequency", collector.Id, diagnostics);

            if (!string.IsNullOrEmpty(collector.Control))
            {
                if (!controlIds.Contains(collector.Control))
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' references unknown Control id "
                            + $"'{collector.Control}'.",
                    });
                }
                else
                {
                    // Track only resolved controls, so the missing-evaluation check below never fires
                    // for an id no document defines (that stays a pure unknown-control diagnostic).
                    controlsWithCollector.Add(collector.Control);
                }
            }

            if (!string.IsNullOrEmpty(collector.Vendor) && !vendorIds.Contains(collector.Vendor))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' references unknown Vendor id "
                        + $"'{collector.Vendor}'.",
                });
            }

            if (!string.IsNullOrEmpty(collector.Type) && !CollectorTypeTokens.Contains(collector.Type))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' has unknown type "
                        + $"'{collector.Type}'. Expected one of: integration, script, manual-attestation, "
                        + "training-attestation, agent.",
                });
            }

            if (!string.IsNullOrEmpty(collector.Frequency) && !EvidenceCollectorFrequency.Tokens.Contains(collector.Frequency))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' has unknown frequency "
                        + $"'{collector.Frequency}'. Expected one of: continuous, daily, weekly, monthly, quarterly, annual.",
                });
            }

            // threshold is optional; when present it must be an integer percent in [0, 100]. Parsing the
            // raw authored text here (not at YAML bind time) turns a malformed value into this diagnostic
            // instead of a binding crash.
            if (!string.IsNullOrWhiteSpace(collector.Threshold)
                && (!int.TryParse(collector.Threshold, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
                    || threshold < 0 || threshold > 100))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' has invalid threshold "
                        + $"'{collector.Threshold}'. Expected an integer percent from 0 to 100.",
                });
            }

            // connection and checks are conditional on type: an integration collector requires both; any
            // other type must declare neither (a connection or checks off the integration path is dead).
            if (string.Equals(collector.Type, "integration", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(collector.Connection))
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' has type 'integration' but is "
                            + "missing required field 'connection'.",
                    });
                }
                else if (!connectionIds.Contains(collector.Connection))
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' references unknown "
                            + $"{GitOpsSchema.KindIntegrationConnection} id '{collector.Connection}'.",
                    });
                }

                if (collector.Checks.Count == 0)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' has type 'integration' but is "
                            + "missing a non-empty 'checks'.",
                    });
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(collector.Connection))
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' declares 'connection', which is "
                            + "only valid for a type 'integration' collector.",
                    });
                }

                if (collector.Checks.Count > 0)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' declares 'checks', which are "
                            + "only valid for a type 'integration' collector.",
                    });
                }
            }

            ValidateCollectorChecks(collector, diagnostics);

            if (!string.IsNullOrEmpty(collector.Id) && !seenIds.Add(collector.Id))
            {
                diagnostics.Add(Dup(GitOpsSchema.KindEvidenceCollector, collector.Id));
            }
        }

        // A control with at least one attached collector must declare an evaluation rule. Iterate the
        // real controls and test membership in the attached set, not the collectors' control-refs, so an
        // unresolved control-ref cannot raise a spurious missing-evaluation diagnostic for an undefined id.
        foreach (var control in config.Controls.Where(c =>
            controlsWithCollector.Contains(c.Id) && string.IsNullOrWhiteSpace(c.Evaluation)))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindControl} '{Describe(control.Id)}' has attached evidence-collectors but is "
                    + "missing required field 'evaluation'.",
            });
        }
    }

    private static void ValidateCollectorChecks(EvidenceCollector collector, List<Diagnostic> diagnostics)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenSourceKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var check in collector.Checks)
        {
            CheckRequired(check.SourceKey, GitOpsSchema.KindEvidenceCollector, "check source_key", collector.Id, diagnostics);
            CheckRequired(check.Name, GitOpsSchema.KindEvidenceCollector, "check name", collector.Id, diagnostics);
            CheckRequired(check.Severity, GitOpsSchema.KindEvidenceCollector, "check severity", collector.Id, diagnostics);

            if (!string.IsNullOrEmpty(check.Severity) && !CheckSeverityTokens.Contains(check.Severity))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' check '{Describe(check.Name)}' has "
                        + $"unknown severity '{check.Severity}'. Expected 'Hard' or 'Soft'.",
                });
            }

            if (!string.IsNullOrEmpty(check.Name) && !seenNames.Add(check.Name))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' has duplicate check name "
                        + $"'{check.Name}'.",
                });
            }

            if (!string.IsNullOrEmpty(check.SourceKey) && !seenSourceKeys.Add(check.SourceKey))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindEvidenceCollector} '{Describe(collector.Id)}' has duplicate check source_key "
                        + $"'{check.SourceKey}'.",
                });
            }
        }
    }

    private static void ValidateAttestationTemplates(
        GitOpsConfig config,
        HashSet<string> controlIds,
        List<Diagnostic> diagnostics)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var template in config.AttestationTemplates)
        {
            CheckApiVersion(template.ApiVersion, GitOpsSchema.KindAttestationTemplate, template.Id, diagnostics);
            CheckRequired(template.Id, GitOpsSchema.KindAttestationTemplate, "id", template.Title, diagnostics);
            CheckRequired(template.Title, GitOpsSchema.KindAttestationTemplate, "title", template.Id, diagnostics);
            CheckRequired(template.Control, GitOpsSchema.KindAttestationTemplate, "control", template.Id, diagnostics);
            CheckRequired(template.Type, GitOpsSchema.KindAttestationTemplate, "type", template.Id, diagnostics);

            if (!string.IsNullOrEmpty(template.Control) && !controlIds.Contains(template.Control))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' references unknown Control id "
                        + $"'{template.Control}'.",
                });
            }

            var typeParsed = !string.IsNullOrEmpty(template.Type) && AttestationTypeTokens.Contains(template.Type);
            if (!string.IsNullOrEmpty(template.Type) && !typeParsed)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' has unknown type "
                        + $"'{template.Type}'. Expected 'manual' or 'training'.",
                });
            }

            // pass_mark is optional here; when present it must be an integer percent in [0, 100]. Parsing the
            // raw authored text turns a malformed value into a diagnostic instead of a YAML binding crash.
            var hasPassMark = !string.IsNullOrWhiteSpace(template.PassMark);
            if (hasPassMark
                && (!int.TryParse(template.PassMark, NumberStyles.Integer, CultureInfo.InvariantCulture, out var passMark)
                    || passMark < 0 || passMark > 100))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' has invalid pass_mark "
                        + $"'{template.PassMark}'. Expected an integer percent from 0 to 100.",
                });
            }

            ValidateAttestationFields(template, diagnostics);
            ValidateAttestationQuiz(template, diagnostics);

            // Type-conditional rules: training needs a pass mark and a quiz to grade against; manual has
            // neither, so declaring them is an authoring mistake.
            var hasQuiz = template.Quiz.Count > 0;
            if (template.Type == "training")
            {
                if (!hasPassMark)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' has type 'training' but is "
                            + "missing required field 'pass_mark'.",
                    });
                }

                if (!hasQuiz)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' has type 'training' but is "
                            + "missing a non-empty 'quiz'.",
                    });
                }
            }
            else if (template.Type == "manual")
            {
                if (hasPassMark)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' has type 'manual' but declares "
                            + "'pass_mark', which is only valid for a training template.",
                    });
                }

                if (hasQuiz)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' has type 'manual' but declares "
                            + "a 'quiz', which is only valid for a training template.",
                    });
                }
            }

            if (!string.IsNullOrEmpty(template.Id) && !seenIds.Add(template.Id))
            {
                diagnostics.Add(Dup(GitOpsSchema.KindAttestationTemplate, template.Id));
            }
        }
    }

    private static void ValidateAttestationFields(AttestationTemplate template, List<Diagnostic> diagnostics)
    {
        var seenFieldIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in template.Fields)
        {
            CheckRequired(field.Id, GitOpsSchema.KindAttestationTemplate, "field id", template.Id, diagnostics);
            CheckRequired(field.Label, GitOpsSchema.KindAttestationTemplate, "field label", template.Id, diagnostics);
            CheckRequired(field.Type, GitOpsSchema.KindAttestationTemplate, "field type", template.Id, diagnostics);

            if (!string.IsNullOrEmpty(field.Id) && !seenFieldIds.Add(field.Id))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' has duplicate field id "
                        + $"'{field.Id}'.",
                });
            }

            var typeKnown = !string.IsNullOrEmpty(field.Type) && FieldTypeTokens.Contains(field.Type);
            if (!string.IsNullOrEmpty(field.Type) && !typeKnown)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' field '{Describe(field.Id)}' has "
                        + $"unknown type '{field.Type}'. Expected one of: boolean, single-choice, short-text.",
                });
            }

            if (field.Type == "single-choice")
            {
                if (field.Options.Count < 2)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' field '{Describe(field.Id)}' is "
                            + "single-choice but has fewer than two options.",
                    });
                }

                CheckDuplicateOptions(template.Id, field.Id, "field", field.Options, diagnostics);
            }
            else if (typeKnown && field.Options.Count > 0)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' field '{Describe(field.Id)}' has "
                        + $"type '{field.Type}' but declares options, which are only valid for a single-choice field.",
                });
            }
        }
    }

    private static void ValidateAttestationQuiz(AttestationTemplate template, List<Diagnostic> diagnostics)
    {
        var seenQuizIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in template.Quiz)
        {
            CheckRequired(item.Id, GitOpsSchema.KindAttestationTemplate, "quiz id", template.Id, diagnostics);
            CheckRequired(item.Prompt, GitOpsSchema.KindAttestationTemplate, "quiz prompt", template.Id, diagnostics);
            CheckRequired(item.Answer, GitOpsSchema.KindAttestationTemplate, "quiz answer", template.Id, diagnostics);

            if (!string.IsNullOrEmpty(item.Id) && !seenQuizIds.Add(item.Id))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' has duplicate quiz id "
                        + $"'{item.Id}'.",
                });
            }

            if (item.Options.Count < 2)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' quiz item '{Describe(item.Id)}' has "
                        + "fewer than two options.",
                });
            }

            CheckDuplicateOptions(template.Id, item.Id, "quiz item", item.Options, diagnostics);

            // The answer is a value reference into the option labels; option-label uniqueness makes it
            // unambiguous. Only check membership when an answer is present (a blank is caught above).
            if (!string.IsNullOrEmpty(item.Answer) && !item.Options.Contains(item.Answer, StringComparer.Ordinal))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(template.Id)}' quiz item '{Describe(item.Id)}' has "
                        + $"answer '{item.Answer}' that is not one of its options.",
                });
            }
        }
    }

    private static void CheckDuplicateOptions(
        string templateId, string ownerId, string ownerKind, IReadOnlyList<string> options, List<Diagnostic> diagnostics)
    {
        var duplicates = options
            .GroupBy(option => option, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicate in duplicates)
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindAttestationTemplate} '{Describe(templateId)}' {ownerKind} '{Describe(ownerId)}' has "
                    + $"duplicate option '{duplicate}'.",
            });
        }
    }

    /// <summary>Parses an asset type case-sensitively (identity is exact-byte).</summary>
    public static bool TryParseAssetType(string value, out AssetKind type)
    {
        switch (value)
        {
            case nameof(AssetKind.Company):
                type = AssetKind.Company;
                return true;
            case nameof(AssetKind.Department):
                type = AssetKind.Department;
                return true;
            case nameof(AssetKind.Machine):
                type = AssetKind.Machine;
                return true;
            case nameof(AssetKind.Vendor):
                type = AssetKind.Vendor;
                return true;
            default:
                type = default;
                return false;
        }
    }

    /// <summary>Parses an asset source token (<c>declared</c>/<c>discovered</c>, exact-byte lowercase).</summary>
    public static bool TryParseAssetSource(string value, out AssetSource source)
    {
        switch (value)
        {
            case "declared":
                source = AssetSource.Declared;
                return true;
            case "discovered":
                source = AssetSource.Discovered;
                return true;
            default:
                source = default;
                return false;
        }
    }

    /// <summary>Parses an organisation kind case-sensitively (identity is exact-byte).</summary>
    public static bool TryParseKind(string value, out OrganisationKind kind)
    {
        switch (value)
        {
            case nameof(OrganisationKind.Company):
                kind = OrganisationKind.Company;
                return true;
            case nameof(OrganisationKind.Department):
                kind = OrganisationKind.Department;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>Parses a scope disposition case-sensitively (identity is exact-byte).</summary>
    public static bool TryParseDisposition(string value, out ScopeDisposition disposition)
    {
        switch (value)
        {
            case nameof(ScopeDisposition.In):
                disposition = ScopeDisposition.In;
                return true;
            case nameof(ScopeDisposition.Out):
                disposition = ScopeDisposition.Out;
                return true;
            default:
                disposition = default;
                return false;
        }
    }

    private static void CheckApiVersion(string apiVersion, string kind, string id, List<Diagnostic> diagnostics)
    {
        if (apiVersion != GitOpsSchema.ApiVersion)
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{kind} '{Describe(id)}' has unknown apiVersion '{apiVersion}'. Expected '{GitOpsSchema.ApiVersion}'.",
            });
        }
    }

    private static void CheckRequired(string value, string kind, string field, string otherId, List<Diagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{kind} '{Describe(otherId)}' is missing required field '{field}'.",
            });
        }
    }

    private static void CheckNoDuplicateRefs(
        IReadOnlyList<string> refs,
        string kind,
        string id,
        string field,
        string targetKind,
        List<Diagnostic> diagnostics)
    {
        // Ordinal equality, consistent with id identity. The join table has a composite
        // PK, so a duplicate would fail import with a duplicate-key error; reject it here
        // as an input error instead. One diagnostic per duplicated id.
        var duplicates = refs
            .GroupBy(reference => reference, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var reference in duplicates)
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{kind} '{Describe(id)}' {field} lists duplicate {targetKind} id '{reference}'.",
            });
        }
    }

    private static bool IsAbsoluteHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static Diagnostic Dup(string kind, string id) => new()
    {
        Message = $"Duplicate {kind} id '{id}'.",
    };

    private static string Describe(string id) => string.IsNullOrEmpty(id) ? "(no id)" : id;
}
