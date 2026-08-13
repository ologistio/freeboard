using System.Globalization;
using Freeboard.Core.Assets;

namespace Freeboard.Core.GitOps;

/// <summary>
/// Validates a loaded <see cref="GitOpsConfig"/>. Collects every error as a
/// <see cref="Diagnostic"/>; never throws and never writes output. Owns: required
/// fields, apiVersion value, unique id per kind, reference resolution, the asset set (type/source
/// tokens, mutually-exclusive parent/owner edges with their target/carrier-type rules, the Vendor-only
/// tier and data_classes vocabularies, the Vendor-only assurances with their resolvable standard
/// reference, expiry date, and warning-window override, and dangling edges, parent cycles, missing read
/// anchors, and a tierless vendor as non-blocking warnings), the scope mapping
/// (exactly-one target of standard/requirement/control, resolvable target references, a scalar
/// dangling-tolerant subject warned when it resolves to no asset, the Vendor-subject-no-standard rule,
/// disposition enum, justification required when Out, and the three unique subject/target pairs), the
/// integration-connections (required fields, closed provider token, absolute base_url,
/// discovery_cadence token, optional vendor reference, and a configuration-key-safe id: no ':' or '__',
/// no case-insensitive collision), and the collectors (resolvable control/vendor references,
/// type/frequency/threshold checks, the control evaluation rule required once a control has an attached
/// collector of any type, the type-conditional provider/connection rules and the provider cross-check
/// against the referenced connection, each tracked check's shape and severity token, the form and quiz
/// shape, and the pass_mark range). Does NOT re-check kind (the loader owns kind-routing) and does NOT
/// own the config key set: which keys a collector may carry is <see cref="CollectorConfigSchema"/>'s,
/// with the loader rejecting an unregistered key and this validator enforcing a registered required one.
/// </summary>
public static class ConfigValidator
{
    /// <summary>Closed token set for a control's evaluation rule (case-sensitive).</summary>
    private static readonly HashSet<string> EvaluationTokens = new(StringComparer.Ordinal) { "all", "any", "manual" };

    /// <summary>
    /// Closed token set for a collector's type (case-sensitive). Public because it is one half of the
    /// key space <see cref="CollectorConfigSchema"/> must cover: a valid pair with no registered schema
    /// silently reopens free-form config, so the completeness pin enumerates this set rather than
    /// restating the registered pairs.
    /// </summary>
    public static readonly IReadOnlySet<string> CollectorTypeTokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "integration", "script", "agent", "manual", "training",
    };

    /// <summary>Closed token set for a form field's type (case-sensitive).</summary>
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
        var assets = ValidateAssets(config, standardIds, diagnostics);
        var vendorIds = assets.VendorIds;
        ValidateScopes(config, assets.AllIds, vendorIds, standardIds, requirementIds, controlIds, diagnostics);
        // Integration-connections consume vendor ids (for the optional vendor reference) and produce the
        // connection provider map the collectors then resolve their connection reference against and
        // cross-check their own authored provider with.
        var connectionProviders = ValidateIntegrationConnections(config, vendorIds, diagnostics);
        ValidateCollectors(config, controlIds, vendorIds, connectionProviders, diagnostics);

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

    /// <summary>The typed id subsets a validated asset set exposes to the reference phases.
    /// <paramref name="AllIds"/> is every asset id (any type), for the scope subject-dangling check.</summary>
    private sealed record AssetIdSets(HashSet<string> OrganisationIds, HashSet<string> VendorIds, HashSet<string> AllIds);

    private static AssetIdSets ValidateAssets(
        GitOpsConfig config, HashSet<string> standardIds, List<Diagnostic> diagnostics)
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

            ValidateVendorRiskProfile(asset, typeParsed, type, diagnostics);
            ValidateVendorAssurances(asset, typeParsed, type, standardIds, diagnostics);

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
        return new AssetIdSets(organisationIds, vendorIds, allIds);
    }

    // tier and data_classes are the vendor risk profile: both are Vendor-only, and both are validated
    // against closed sets held in Freeboard.Core with no store read, so the check runs offline. A missing
    // tier only warns, matching the missing-owner warning: if the edge that decides whether anyone can see
    // the vendor does not fail a sync, the field that colors a tag must not either. A missing or empty
    // data_classes is silent - a vendor holding none of your regulated data is a real state.
    private static void ValidateVendorRiskProfile(
        Asset asset, bool typeParsed, AssetKind type, List<Diagnostic> diagnostics)
    {
        var hasTier = !string.IsNullOrWhiteSpace(asset.Tier);
        if (typeParsed && type != AssetKind.Vendor && (hasTier || asset.DataClasses.Count > 0))
        {
            var field = hasTier ? "tier" : "data_classes";
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' sets '{field}' but is not a Vendor; "
                    + "only a vendor carries a risk profile.",
            });
        }

        if (hasTier && !TryParseVendorTier(asset.Tier, out _))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' has unknown tier '{asset.Tier}'. "
                    + $"Expected '{nameof(VendorTier.Critical)}', '{nameof(VendorTier.High)}', "
                    + $"'{nameof(VendorTier.Medium)}', or '{nameof(VendorTier.Low)}'.",
            });
        }

        var seenClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dataClass in asset.DataClasses)
        {
            if (!VendorDataClass.Tokens.Contains(dataClass))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' has unknown data class "
                        + $"'{dataClass}'. Expected one of: {string.Join(", ", VendorDataClass.Tokens)}.",
                });
                continue;
            }

            // Reported rather than silently deduped: a repeated token is an authoring mistake, and
            // collapsing it hides one, matching duplicate check names and duplicate requirement ids.
            if (!seenClasses.Add(dataClass))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' has duplicate data class "
                        + $"'{dataClass}'.",
                });
            }
        }

        if (typeParsed && type == AssetKind.Vendor && !hasTier)
        {
            diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' is a Vendor with no tier; the register "
                    + "shows it as untracked until a tier is set.",
            });
        }
    }

    // Assurances are Vendor-only, like the risk profile. The standard reference keeps referential
    // integrity (matching the Scope target references) rather than warning as a dangling parent or owner
    // does: a certification names a document in the same config, not a thing another writer owns. An
    // expiry already in the past draws NO diagnostic - validation reads no clock, so a time-dependent
    // check would make two runs over one unchanged config disagree.
    private static void ValidateVendorAssurances(
        Asset asset,
        bool typeParsed,
        AssetKind type,
        HashSet<string> standardIds,
        List<Diagnostic> diagnostics)
    {
        if (asset.Assurances.Count == 0)
        {
            return;
        }

        if (typeParsed && type != AssetKind.Vendor)
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' sets 'assurances' but is not a Vendor; "
                    + "only a vendor holds certifications.",
            });
        }

        var seenStandards = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assurance in asset.Assurances)
        {
            if (string.IsNullOrWhiteSpace(assurance.Standard))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' has an assurance with no 'standard'.",
                });
            }
            else
            {
                // Uniqueness is checked whether or not the standard resolves. Two entries naming the
                // same missing standard are two mistakes, and reporting only the dangling reference
                // would leave the duplicate behind once the author declared the standard.
                if (!standardIds.Contains(assurance.Standard))
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' has an assurance referencing unknown "
                            + $"Standard id '{assurance.Standard}'.",
                    });
                }

                if (!seenStandards.Add(assurance.Standard))
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' has duplicate assurance standard "
                            + $"'{assurance.Standard}'.",
                    });
                }
            }

            if (string.IsNullOrWhiteSpace(assurance.Expires))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' assurance "
                        + $"'{Describe(assurance.Standard)}' is missing required field 'expires'.",
                });
            }
            else if (!DateOnly.TryParseExact(
                assurance.Expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' assurance "
                        + $"'{Describe(assurance.Standard)}' has malformed expires '{assurance.Expires}'. "
                        + "Expected a calendar date in YYYY-MM-DD form.",
                });
            }

            // Zero is legitimate and means no advance notice; only a non-integer or a negative is a mistake.
            if (!string.IsNullOrWhiteSpace(assurance.WarnDays)
                && (!int.TryParse(assurance.WarnDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out var warnDays)
                    || warnDays < 0))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindAsset} '{Describe(asset.Id)}' assurance "
                        + $"'{Describe(assurance.Standard)}' has invalid warn_days '{assurance.WarnDays}'. "
                        + "Expected a whole number of zero or more days.",
                });
            }
        }
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

    // The unified scope validator. A scope names one asset subject and exactly one target (a standard,
    // requirement, or control). The subject reference is scalar and dangling-tolerant: a subject naming no
    // asset is a non-blocking Warning (like a dangling asset edge), tagged so the CLI sync path can defer to
    // the importer's DB-accurate check. The three target references keep real FKs, so a dangling target is
    // an Error. A Vendor subject cannot target a standard.
    private static void ValidateScopes(
        GitOpsConfig config,
        HashSet<string> allAssetIds,
        HashSet<string> vendorIds,
        HashSet<string> standardIds,
        HashSet<string> requirementIds,
        HashSet<string> controlIds,
        List<Diagnostic> diagnostics)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenStandardPairs = new HashSet<(string, string)>();
        var seenRequirementPairs = new HashSet<(string, string)>();
        var seenControlPairs = new HashSet<(string, string)>();

        foreach (var scope in config.Scopes)
        {
            CheckApiVersion(scope.ApiVersion, GitOpsSchema.KindScope, scope.Id, diagnostics);
            CheckRequired(scope.Id, GitOpsSchema.KindScope, "id", scope.Title, diagnostics);
            CheckRequired(scope.Title, GitOpsSchema.KindScope, "title", scope.Id, diagnostics);
            CheckRequired(scope.Subject, GitOpsSchema.KindScope, "subject", scope.Id, diagnostics);
            CheckRequired(scope.Disposition, GitOpsSchema.KindScope, "disposition", scope.Id, diagnostics);

            var hasStandard = !string.IsNullOrWhiteSpace(scope.Standard);
            var hasRequirement = !string.IsNullOrWhiteSpace(scope.Requirement);
            var hasControl = !string.IsNullOrWhiteSpace(scope.Control);
            var targetCount = (hasStandard ? 1 : 0) + (hasRequirement ? 1 : 0) + (hasControl ? 1 : 0);
            if (targetCount != 1)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' must name exactly one of "
                        + "'standard', 'requirement', or 'control'.",
                });
            }

            if (hasStandard && !standardIds.Contains(scope.Standard))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' references unknown Standard id "
                        + $"'{scope.Standard}'.",
                });
            }

            if (hasRequirement && !requirementIds.Contains(scope.Requirement))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' references unknown Requirement id "
                        + $"'{scope.Requirement}'.",
                });
            }

            if (hasControl && !controlIds.Contains(scope.Control))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' references unknown Control id "
                        + $"'{scope.Control}'.",
                });
            }

            // A subject naming no asset is a tolerated Warning: a subject may name a discovered asset a
            // later sync removes, or one not yet discovered. Tagged so the sync path can defer to the
            // importer's DB-accurate result. A resolving subject that is a Vendor cannot target a standard.
            var subjectResolves = !string.IsNullOrEmpty(scope.Subject) && allAssetIds.Contains(scope.Subject);
            if (!string.IsNullOrEmpty(scope.Subject) && !subjectResolves)
            {
                diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Code = DiagnosticCode.ScopeSubjectUnresolved,
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' has subject '{scope.Subject}' that "
                        + "resolves to no asset.",
                });
            }
            else if (subjectResolves && hasStandard && vendorIds.Contains(scope.Subject))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' subject '{scope.Subject}' is a Vendor "
                        + "and cannot target a standard.",
                });
            }

            var dispositionParsed = TryParseDisposition(scope.Disposition, out var disposition);
            if (!string.IsNullOrEmpty(scope.Disposition) && !dispositionParsed)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' has unknown disposition "
                        + $"'{scope.Disposition}'. Expected '{nameof(ScopeDisposition.In)}' or "
                        + $"'{nameof(ScopeDisposition.Out)}'.",
                });
            }

            // An Out exception must carry its rationale. An In scope may omit it.
            if (dispositionParsed && disposition == ScopeDisposition.Out
                && string.IsNullOrWhiteSpace(scope.Justification))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' has disposition 'Out' but is "
                        + "missing required field 'justification'.",
                });
            }

            if (!string.IsNullOrEmpty(scope.Id) && !seenIds.Add(scope.Id))
            {
                diagnostics.Add(Dup(GitOpsSchema.KindScope, scope.Id));
            }

            if (!string.IsNullOrEmpty(scope.Subject) && hasStandard
                && !seenStandardPairs.Add((scope.Subject, scope.Standard)))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} maps subject '{scope.Subject}' to standard "
                        + $"'{scope.Standard}' more than once.",
                });
            }

            if (!string.IsNullOrEmpty(scope.Subject) && hasRequirement
                && !seenRequirementPairs.Add((scope.Subject, scope.Requirement)))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} maps subject '{scope.Subject}' to requirement "
                        + $"'{scope.Requirement}' more than once.",
                });
            }

            if (!string.IsNullOrEmpty(scope.Subject) && hasControl
                && !seenControlPairs.Add((scope.Subject, scope.Control)))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} maps subject '{scope.Subject}' to control "
                        + $"'{scope.Control}' more than once.",
                });
            }
        }
    }

    /// <summary>
    /// Validates the integration-connections and returns each connection's provider by id (first
    /// occurrence wins), which a collector's connection reference resolves against and its authored
    /// provider is cross-checked with.
    /// </summary>
    private static Dictionary<string, string> ValidateIntegrationConnections(
        GitOpsConfig config,
        HashSet<string> vendorIds,
        List<Diagnostic> diagnostics)
    {
        var providerById = new Dictionary<string, string>(StringComparer.Ordinal);
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
                && !CollectorFrequency.Tokens.Contains(connection.DiscoveryCadence))
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

                providerById.TryAdd(connection.Id, connection.Provider);
            }
        }

        return providerById;
    }

    private static void ValidateCollectors(
        GitOpsConfig config,
        HashSet<string> controlIds,
        HashSet<string> vendorIds,
        IReadOnlyDictionary<string, string> connectionProviders,
        List<Diagnostic> diagnostics)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var controlsWithCollector = new HashSet<string>(StringComparer.Ordinal);

        foreach (var collector in config.Collectors)
        {
            CheckApiVersion(collector.ApiVersion, GitOpsSchema.KindCollector, collector.Id, diagnostics);
            CheckRequired(collector.Id, GitOpsSchema.KindCollector, "id", collector.Title, diagnostics);
            CheckRequired(collector.Title, GitOpsSchema.KindCollector, "title", collector.Id, diagnostics);
            CheckRequired(collector.Control, GitOpsSchema.KindCollector, "control", collector.Id, diagnostics);
            CheckRequired(collector.Type, GitOpsSchema.KindCollector, "type", collector.Id, diagnostics);
            // frequency is required on every type, attestations included: evidence ingest stamps the
            // resolved collector's cadence onto each run it appends and staleness is judged from that
            // stamp, so a cadence-less collector would post evidence that could never read as stale.
            CheckRequired(collector.Frequency, GitOpsSchema.KindCollector, "frequency", collector.Id, diagnostics);

            if (!string.IsNullOrEmpty(collector.Control))
            {
                if (!controlIds.Contains(collector.Control))
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' references unknown Control id "
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
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' references unknown Vendor id "
                        + $"'{collector.Vendor}'.",
                });
            }

            if (!string.IsNullOrEmpty(collector.Type) && !CollectorTypeTokens.Contains(collector.Type))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has unknown type "
                        + $"'{collector.Type}'. Expected one of: integration, script, agent, manual, training.",
                });
            }

            if (!string.IsNullOrEmpty(collector.Frequency) && !CollectorFrequency.Tokens.Contains(collector.Frequency))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has unknown frequency "
                        + $"'{collector.Frequency}'. Expected one of: continuous, daily, weekly, monthly, quarterly, annual.",
                });
            }

            // threshold is optional; when present it must be an integer percent in [0, 100]. Parsing the
            // raw authored text here (not at YAML bind time) turns a malformed value into this diagnostic
            // instead of a binding crash. pass_mark below is the same rule one level in, on the config.
            if (!string.IsNullOrWhiteSpace(collector.Threshold)
                && (!int.TryParse(collector.Threshold, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
                    || threshold < 0 || threshold > 100))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has invalid threshold "
                        + $"'{collector.Threshold}'. Expected an integer percent from 0 to 100.",
                });
            }

            // The blank guard is what makes a blank pass mark a missing required key rather than a range
            // error: the registry's required-key check reports it, and this range check stays silent.
            if (!string.IsNullOrWhiteSpace(collector.Config.PassMark)
                && (!int.TryParse(collector.Config.PassMark, NumberStyles.Integer, CultureInfo.InvariantCulture, out var passMark)
                    || passMark < 0 || passMark > 100))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has invalid pass_mark "
                        + $"'{collector.Config.PassMark}'. Expected an integer percent from 0 to 100.",
                });
            }

            ValidateCollectorProviderAndConnection(collector, connectionProviders, diagnostics);
            ValidateRequiredConfigKeys(collector, diagnostics);
            ValidateCollectorChecks(collector, diagnostics);
            ValidateCollectorFields(collector, diagnostics);
            ValidateCollectorQuiz(collector, diagnostics);

            if (!string.IsNullOrEmpty(collector.Id) && !seenIds.Add(collector.Id))
            {
                diagnostics.Add(Dup(GitOpsSchema.KindCollector, collector.Id));
            }
        }

        // A control with at least one attached collector, of any type, must declare an evaluation rule.
        // Iterate the real controls and test membership in the attached set, not the collectors'
        // control-refs, so an unresolved control-ref cannot raise a spurious missing-evaluation
        // diagnostic for an undefined id.
        foreach (var control in config.Controls.Where(c =>
            controlsWithCollector.Contains(c.Id) && string.IsNullOrWhiteSpace(c.Evaluation)))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindControl} '{Describe(control.Id)}' has attached collectors but is "
                    + "missing required field 'evaluation'.",
            });
        }
    }

    // provider and connection are conditional on type: an integration collector requires both and its
    // provider must equal the referenced connection's; any other type must declare neither (both are
    // dead off the integration path). Authoring the provider rather than deriving it is what makes the
    // config schema key resolvable from the document alone, even when the connection does not resolve;
    // this cross-check is what stops the two disagreeing silently.
    private static void ValidateCollectorProviderAndConnection(
        Collector collector,
        IReadOnlyDictionary<string, string> connectionProviders,
        List<Diagnostic> diagnostics)
    {
        if (!string.Equals(collector.Type, "integration", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(collector.Provider))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' declares 'provider', which is "
                        + "only valid for a type 'integration' collector.",
                });
            }

            if (!string.IsNullOrWhiteSpace(collector.Connection))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' declares 'connection', which is "
                        + "only valid for a type 'integration' collector.",
                });
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(collector.Provider))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has type 'integration' but is "
                    + "missing required field 'provider'.",
            });
        }
        else if (!IntegrationProvider.Tokens.Contains(collector.Provider))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has unknown provider "
                    + $"'{collector.Provider}'. Expected one of: fleet.",
            });
        }

        if (string.IsNullOrWhiteSpace(collector.Connection))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has type 'integration' but is "
                    + "missing required field 'connection'.",
            });
            return;
        }

        if (!connectionProviders.TryGetValue(collector.Connection, out var connectionProvider))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' references unknown "
                    + $"{GitOpsSchema.KindIntegrationConnection} id '{collector.Connection}'.",
            });
            return;
        }

        // A connection with NO provider is already reported as missing a required field, and a mismatch
        // naming its empty value would be that one mistake reported twice. A connection whose provider is
        // present but outside the token set is deliberately NOT skipped: the two documents genuinely name
        // different providers, and repairing the collector's token would leave the connection's unknown
        // one in place, so the disagreement is a second fact the author needs.
        if (string.IsNullOrWhiteSpace(connectionProvider))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(collector.Provider)
            && !string.Equals(collector.Provider, connectionProvider, StringComparison.Ordinal))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has provider '{collector.Provider}' "
                    + $"but its {GitOpsSchema.KindIntegrationConnection} '{collector.Connection}' has provider "
                    + $"'{connectionProvider}'.",
            });
        }
    }

    // The registered (type, provider) schema is the sole owner of the key set, so nothing here names a
    // key: it asks the registry which keys the pair requires and whether the parsed member carries a
    // value. A key the schema does not register is rejected by the loader instead, on the authored
    // mapping, because an empty value parses to the same member as an absent one. No schema resolves when
    // the type or provider token is unknown, or when an integration collector omits its provider, and the
    // token diagnostic is then the only one the author gets: which keys the pair requires is a property of
    // the pair, so an unresolved pair has no requiredness to report without hard-coding one provider's. A
    // provider authored on a type that cannot carry one is not such a case - it selects nothing, so the
    // schema still resolves on the type and the required-key check still runs.
    private static void ValidateRequiredConfigKeys(Collector collector, List<Diagnostic> diagnostics)
    {
        var schema = CollectorConfigSchema.For(collector.Type, collector.Provider);
        if (schema is null)
        {
            return;
        }

        foreach (var key in schema.Where(k => k.Required && !CollectorConfigSchema.HasValue(collector.Config, k.Name)))
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' is missing required config key "
                    + $"'{key.Name}'.",
            });
        }
    }

    private static void ValidateCollectorChecks(Collector collector, List<Diagnostic> diagnostics)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenSourceKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var check in collector.Config.Checks)
        {
            CheckRequired(check.SourceKey, GitOpsSchema.KindCollector, "check source_key", collector.Id, diagnostics);
            CheckRequired(check.Name, GitOpsSchema.KindCollector, "check name", collector.Id, diagnostics);
            CheckRequired(check.Severity, GitOpsSchema.KindCollector, "check severity", collector.Id, diagnostics);

            if (!string.IsNullOrEmpty(check.Severity) && !CheckSeverityTokens.Contains(check.Severity))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' check '{Describe(check.Name)}' has "
                        + $"unknown severity '{check.Severity}'. Expected 'Hard' or 'Soft'.",
                });
            }

            if (!string.IsNullOrEmpty(check.Name) && !seenNames.Add(check.Name))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has duplicate check name "
                        + $"'{check.Name}'.",
                });
            }

            if (!string.IsNullOrEmpty(check.SourceKey) && !seenSourceKeys.Add(check.SourceKey))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has duplicate check source_key "
                        + $"'{check.SourceKey}'.",
                });
            }
        }
    }

    private static void ValidateCollectorFields(Collector collector, List<Diagnostic> diagnostics)
    {
        var seenFieldIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in collector.Config.Fields)
        {
            CheckRequired(field.Id, GitOpsSchema.KindCollector, "field id", collector.Id, diagnostics);
            CheckRequired(field.Label, GitOpsSchema.KindCollector, "field label", collector.Id, diagnostics);
            CheckRequired(field.Type, GitOpsSchema.KindCollector, "field type", collector.Id, diagnostics);

            if (!string.IsNullOrEmpty(field.Id) && !seenFieldIds.Add(field.Id))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has duplicate field id "
                        + $"'{field.Id}'.",
                });
            }

            var typeKnown = !string.IsNullOrEmpty(field.Type) && FieldTypeTokens.Contains(field.Type);
            if (!string.IsNullOrEmpty(field.Type) && !typeKnown)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' field '{Describe(field.Id)}' has "
                        + $"unknown type '{field.Type}'. Expected one of: boolean, single-choice, short-text.",
                });
            }

            if (field.Type == "single-choice")
            {
                if (field.Options.Count < 2)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' field '{Describe(field.Id)}' is "
                            + "single-choice but has fewer than two options.",
                    });
                }

                CheckDuplicateOptions(collector.Id, field.Id, "field", field.Options, diagnostics);
            }
            else if (typeKnown && field.Options.Count > 0)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' field '{Describe(field.Id)}' has "
                        + $"type '{field.Type}' but declares options, which are only valid for a single-choice field.",
                });
            }
        }
    }

    private static void ValidateCollectorQuiz(Collector collector, List<Diagnostic> diagnostics)
    {
        var seenQuizIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in collector.Config.Quiz)
        {
            CheckRequired(item.Id, GitOpsSchema.KindCollector, "quiz id", collector.Id, diagnostics);
            CheckRequired(item.Prompt, GitOpsSchema.KindCollector, "quiz prompt", collector.Id, diagnostics);
            CheckRequired(item.Answer, GitOpsSchema.KindCollector, "quiz answer", collector.Id, diagnostics);

            if (!string.IsNullOrEmpty(item.Id) && !seenQuizIds.Add(item.Id))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' has duplicate quiz id "
                        + $"'{item.Id}'.",
                });
            }

            if (item.Options.Count < 2)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' quiz item '{Describe(item.Id)}' has "
                        + "fewer than two options.",
                });
            }

            CheckDuplicateOptions(collector.Id, item.Id, "quiz item", item.Options, diagnostics);

            // The answer is a value reference into the option labels; option-label uniqueness makes it
            // unambiguous. Only check membership when an answer is present (a blank is caught above).
            if (!string.IsNullOrEmpty(item.Answer) && !item.Options.Contains(item.Answer, StringComparer.Ordinal))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindCollector} '{Describe(collector.Id)}' quiz item '{Describe(item.Id)}' has "
                        + $"answer '{item.Answer}' that is not one of its options.",
                });
            }
        }
    }

    private static void CheckDuplicateOptions(
        string collectorId, string ownerId, string ownerKind, IReadOnlyList<string> options, List<Diagnostic> diagnostics)
    {
        var duplicates = options
            .GroupBy(option => option, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicate in duplicates)
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindCollector} '{Describe(collectorId)}' {ownerKind} '{Describe(ownerId)}' has "
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

    /// <summary>Parses a vendor tier case-sensitively (identity is exact-byte).</summary>
    public static bool TryParseVendorTier(string value, out VendorTier tier)
    {
        switch (value)
        {
            case nameof(VendorTier.Critical):
                tier = VendorTier.Critical;
                return true;
            case nameof(VendorTier.High):
                tier = VendorTier.High;
                return true;
            case nameof(VendorTier.Medium):
                tier = VendorTier.Medium;
                return true;
            case nameof(VendorTier.Low):
                tier = VendorTier.Low;
                return true;
            default:
                tier = default;
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
