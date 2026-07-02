namespace Freeboard.Core.GitOps;

/// <summary>
/// Validates a loaded <see cref="GitOpsConfig"/>. Collects every error as a
/// <see cref="Diagnostic"/>; never throws and never writes output. Owns: required
/// fields, apiVersion value, unique id per kind, reference resolution, the organisation
/// tree (acyclic, resolvable parents), and the scope mapping (resolvable references,
/// disposition enum, unique organisation/standard pair). Does NOT re-check kind (the
/// loader owns kind-routing).
/// </summary>
public static class ConfigValidator
{
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
        ValidateControls(config, requirementIds, diagnostics);
        var organisationIds = ValidateOrganisations(config, diagnostics);
        ValidateScopes(config, organisationIds, standardIds, diagnostics);

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

    private static void ValidateControls(
        GitOpsConfig config,
        HashSet<string> requirementIds,
        List<Diagnostic> diagnostics)
    {
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

            if (!string.IsNullOrEmpty(control.Id) && !seen.Add(control.Id))
            {
                diagnostics.Add(Dup(GitOpsSchema.KindControl, control.Id));
            }
        }
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

        var reportedCycle = new HashSet<string>(StringComparer.Ordinal);
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
                    // start participates in a cycle. Report once per cycle member set.
                    if (reportedCycle.Add(start))
                    {
                        diagnostics.Add(new Diagnostic
                        {
                            Message = $"{GitOpsSchema.KindOrganisation} '{Describe(start)}' is part of a parent cycle.",
                        });
                    }

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
