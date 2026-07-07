using System.Globalization;

namespace Freeboard.Core.GitOps;

/// <summary>
/// Validates a loaded <see cref="GitOpsConfig"/>. Collects every error as a
/// <see cref="Diagnostic"/>; never throws and never writes output. Owns: required
/// fields, apiVersion value, unique id per kind, reference resolution, the organisation
/// tree (acyclic, resolvable parents), the scope mapping (resolvable references,
/// disposition enum, unique organisation/standard pair), the requirement-scope mapping
/// (resolvable references, disposition enum, unique organisation/requirement pair), and the
/// vendor-scope mapping (exactly-one target, resolvable references, disposition enum, unique
/// vendor/target pair, justification required when Out), and the evidence-collectors (resolvable
/// control/vendor references, type/frequency/threshold checks, and the control evaluation rule
/// required once a control has an attached collector). Does NOT re-check kind (the loader owns
/// kind-routing).
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

    /// <summary>Closed token set for an evidence-collector's collection cadence (case-sensitive).</summary>
    private static readonly HashSet<string> FrequencyTokens = new(StringComparer.Ordinal)
    {
        "continuous", "daily", "weekly", "monthly", "quarterly", "annual",
    };

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
        var organisationIds = ValidateOrganisations(config, diagnostics);
        ValidateScopes(config, organisationIds, standardIds, diagnostics);
        ValidateRequirementScopes(config, organisationIds, requirementIds, diagnostics);
        var vendorIds = ValidateVendors(config, diagnostics);
        ValidateVendorScopes(config, vendorIds, requirementIds, controlIds, diagnostics);
        ValidateEvidenceCollectors(config, controlIds, vendorIds, diagnostics);

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

    private static HashSet<string> ValidateOrganisations(GitOpsConfig config, List<Diagnostic> diagnostics)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var organisation in config.Organisations)
        {
            CheckApiVersion(organisation.ApiVersion, GitOpsSchema.KindOrganisation, organisation.Id, diagnostics);
            CheckRequired(organisation.Id, GitOpsSchema.KindOrganisation, "id", organisation.Title, diagnostics);
            CheckRequired(organisation.Title, GitOpsSchema.KindOrganisation, "title", organisation.Id, diagnostics);
            CheckRequired(organisation.OrgKind, GitOpsSchema.KindOrganisation, "type", organisation.Id, diagnostics);

            if (!string.IsNullOrEmpty(organisation.OrgKind) && !TryParseKind(organisation.OrgKind, out _))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindOrganisation} '{Describe(organisation.Id)}' has unknown kind "
                        + $"'{organisation.OrgKind}'. Expected '{nameof(OrganisationKind.Company)}' or "
                        + $"'{nameof(OrganisationKind.Department)}'.",
                });
            }

            if (!string.IsNullOrEmpty(organisation.Id))
            {
                if (!seen.Add(organisation.Id))
                {
                    diagnostics.Add(Dup(GitOpsSchema.KindOrganisation, organisation.Id));
                }

                ids.Add(organisation.Id);
            }
        }

        ValidateOrganisationParents(config, ids, diagnostics);
        return ids;
    }

    private static void ValidateOrganisationParents(
        GitOpsConfig config,
        HashSet<string> organisationIds,
        List<Diagnostic> diagnostics)
    {
        // Resolve dangling parents first: a parent naming an id no organisation defines.
        var danglingParents = config.Organisations.Where(organisation =>
            !string.IsNullOrEmpty(organisation.Parent) && !organisationIds.Contains(organisation.Parent));
        foreach (var organisation in danglingParents)
        {
            diagnostics.Add(new Diagnostic
            {
                Message = $"{GitOpsSchema.KindOrganisation} '{Describe(organisation.Id)}' has unknown parent "
                    + $"'{organisation.Parent}'.",
            });
        }

        // Cycle detection over the parent edges. Only follow edges to defined parents so a
        // dangling-parent diagnostic is not double-reported as a cycle. An organisation that
        // names itself is a self-cycle.
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var organisation in config.Organisations)
        {
            if (string.IsNullOrEmpty(organisation.Id) || parentOf.ContainsKey(organisation.Id))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(organisation.Parent) && organisationIds.Contains(organisation.Parent))
            {
                parentOf[organisation.Id] = organisation.Parent;
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
                    // start's parent chain loops back to itself. A multi-node cycle yields one
                    // diagnostic per member, since each member is a distinct start in this loop.
                    diagnostics.Add(new Diagnostic
                    {
                        Message = $"{GitOpsSchema.KindOrganisation} '{Describe(start)}' is part of a parent cycle.",
                    });

                    break;
                }

                node = parent;
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

    private static HashSet<string> ValidateVendors(GitOpsConfig config, List<Diagnostic> diagnostics)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var vendor in config.Vendors)
        {
            CheckApiVersion(vendor.ApiVersion, GitOpsSchema.KindVendor, vendor.Id, diagnostics);
            CheckRequired(vendor.Id, GitOpsSchema.KindVendor, "id", vendor.Title, diagnostics);
            CheckRequired(vendor.Title, GitOpsSchema.KindVendor, "title", vendor.Id, diagnostics);

            if (!string.IsNullOrEmpty(vendor.Id))
            {
                if (!seen.Add(vendor.Id))
                {
                    diagnostics.Add(Dup(GitOpsSchema.KindVendor, vendor.Id));
                }

                ids.Add(vendor.Id);
            }
        }

        return ids;
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

    private static void ValidateEvidenceCollectors(
        GitOpsConfig config,
        HashSet<string> controlIds,
        HashSet<string> vendorIds,
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

            if (!string.IsNullOrEmpty(collector.Frequency) && !FrequencyTokens.Contains(collector.Frequency))
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
