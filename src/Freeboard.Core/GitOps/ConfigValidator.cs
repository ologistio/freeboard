namespace Freeboard.Core.GitOps;

/// <summary>
/// Validates a loaded <see cref="GitOpsConfig"/>. Collects every error as a
/// <see cref="Diagnostic"/>; never throws and never writes output. Owns: required
/// fields, apiVersion value, unique id per kind, and reference resolution. Does
/// NOT re-check kind (the loader owns kind-routing).
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

        var standardIds = ValidateStandards(config, diagnostics);
        var controlIds = ValidateControls(config, standardIds, diagnostics);
        ValidateScopes(config, controlIds, diagnostics);

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

    private static HashSet<string> ValidateControls(
        GitOpsConfig config,
        HashSet<string> standardIds,
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

            foreach (var standardId in control.MapsTo.Where(standardId => !standardIds.Contains(standardId)))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindControl} '{Describe(control.Id)}' maps_to unknown Standard id '{standardId}'.",
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

    private static void ValidateScopes(
        GitOpsConfig config,
        HashSet<string> controlIds,
        List<Diagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scope in config.Scopes)
        {
            CheckApiVersion(scope.ApiVersion, GitOpsSchema.KindScope, scope.Id, diagnostics);
            CheckRequired(scope.Id, GitOpsSchema.KindScope, "id", scope.Title, diagnostics);
            CheckRequired(scope.Title, GitOpsSchema.KindScope, "title", scope.Id, diagnostics);

            if (scope.Controls.Count == 0)
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' is missing required field 'controls'.",
                });
            }

            foreach (var controlId in scope.Controls.Where(controlId => !controlIds.Contains(controlId)))
            {
                diagnostics.Add(new Diagnostic
                {
                    Message = $"{GitOpsSchema.KindScope} '{Describe(scope.Id)}' references unknown Control id '{controlId}'.",
                });
            }

            if (!string.IsNullOrEmpty(scope.Id) && !seen.Add(scope.Id))
            {
                diagnostics.Add(Dup(GitOpsSchema.KindScope, scope.Id));
            }
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

    private static Diagnostic Dup(string kind, string id) => new()
    {
        Message = $"Duplicate {kind} id '{id}'.",
    };

    private static string Describe(string id) => string.IsNullOrEmpty(id) ? "(no id)" : id;
}
