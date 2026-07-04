using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Freeboard.Core.GitOps;

/// <summary>
/// Loads a directory of YAML config files into the typed <see cref="GitOpsConfig"/> model.
/// Never throws on bad input and never writes output: all problems are returned as
/// <see cref="Diagnostic"/> data. Owns kind-routing and unknown-field detection.
/// </summary>
public static class ConfigLoader
{
    // Schema keys per kind. apiVersion/kind are camelCase exceptions; domain fields are snake_case.
    // The top-level `kind` is the document discriminator (Standard/Control/Organisation/Scope). An
    // Organisation's Company/Department distinction is authored under `type` so it does not collide
    // with the discriminator; it persists and reads back as the organisation's `kind`.
    private static readonly IReadOnlyDictionary<string, HashSet<string>> SchemaKeys =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [GitOpsSchema.KindStandard] = new(StringComparer.Ordinal)
            {
                "apiVersion", "kind", "id", "title", "version", "authority", "publisher", "source_url",
            },
            [GitOpsSchema.KindRequirement] = new(StringComparer.Ordinal)
            {
                "apiVersion", "kind", "id", "title", "standard", "theme", "statement", "guidance",
                "citation_label", "citation_url",
            },
            [GitOpsSchema.KindControl] = new(StringComparer.Ordinal) { "apiVersion", "kind", "id", "title", "maps_to" },
            [GitOpsSchema.KindOrganisation] = new(StringComparer.Ordinal) { "apiVersion", "kind", "id", "title", "type", "parent" },
            [GitOpsSchema.KindScope] = new(StringComparer.Ordinal) { "apiVersion", "kind", "id", "title", "organisation", "standard", "disposition" },
            [GitOpsSchema.KindRequirementScope] = new(StringComparer.Ordinal) { "apiVersion", "kind", "id", "title", "organisation", "requirement", "disposition" },
        };

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithAttributeOverride<Standard>(s => s.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        .WithAttributeOverride<Requirement>(r => r.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        .WithAttributeOverride<Control>(c => c.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        .WithAttributeOverride<Organisation>(o => o.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        // The Company/Department value is authored under `type`; it binds to the OrgKind property.
        .WithAttributeOverride<Organisation>(o => o.OrgKind, new YamlMemberAttribute { Alias = "type", ApplyNamingConventions = false })
        .WithAttributeOverride<Scope>(s => s.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        .WithAttributeOverride<RequirementScope>(s => s.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Reads every <c>.yaml</c> file in <paramref name="directory"/> in deterministic order
    /// (files by normalized relative path, ordinal; then in-file document order) and returns
    /// the loaded model with any loader diagnostics. Does not run validation.
    /// </summary>
    public static ConfigResult Load(string directory)
    {
        var config = new GitOpsConfig();
        var diagnostics = new List<Diagnostic>();

        if (!Directory.Exists(directory))
        {
            diagnostics.Add(new Diagnostic { File = directory, Message = $"Config directory not found: {directory}" });
            return new ConfigResult { Config = config, Diagnostics = diagnostics };
        }

        var files = Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: NormalizeRelative(directory, path)))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();

        foreach (var (path, relative) in files)
        {
            LoadFile(path, relative, config, diagnostics);
        }

        return new ConfigResult { Config = config, Diagnostics = diagnostics };
    }

    private static string NormalizeRelative(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative.Replace('\\', '/');
    }

    private static void LoadFile(string path, string relative, GitOpsConfig config, List<Diagnostic> diagnostics)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic { File = relative, Message = $"Could not read file: {ex.Message}" });
            return;
        }

        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(text);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            diagnostics.Add(FromYamlException(relative, ex));
            return;
        }

        foreach (var document in stream.Documents)
        {
            LoadDocument(document, relative, config, diagnostics);
        }
    }

    private static void LoadDocument(YamlDocument document, string relative, GitOpsConfig config, List<Diagnostic> diagnostics)
    {
        if (document.RootNode is not YamlMappingNode mapping)
        {
            diagnostics.Add(new Diagnostic
            {
                File = relative,
                Line = (int)document.RootNode.Start.Line,
                Column = (int)document.RootNode.Start.Column,
                Message = "Document is not a mapping.",
            });
            return;
        }

        var kind = ScalarValue(mapping, "kind");
        if (string.IsNullOrEmpty(kind))
        {
            diagnostics.Add(new Diagnostic
            {
                File = relative,
                Line = (int)mapping.Start.Line,
                Column = (int)mapping.Start.Column,
                Message = "Document has no 'kind'.",
            });
            return;
        }

        if (!SchemaKeys.TryGetValue(kind, out var knownKeys))
        {
            diagnostics.Add(new Diagnostic
            {
                File = relative,
                Line = (int)mapping.Start.Line,
                Column = (int)mapping.Start.Column,
                Message = $"Unknown kind '{kind}'. Expected one of: {GitOpsSchema.KindStandard}, {GitOpsSchema.KindRequirement}, {GitOpsSchema.KindControl}, {GitOpsSchema.KindOrganisation}, {GitOpsSchema.KindScope}, {GitOpsSchema.KindRequirementScope}.",
            });
            return;
        }

        ReportUnknownFields(mapping, kind, knownKeys, relative, diagnostics);

        try
        {
            switch (kind)
            {
                case GitOpsSchema.KindStandard:
                    config.Standards.Add(Deserialize<Standard>(mapping));
                    break;
                case GitOpsSchema.KindRequirement:
                    config.Requirements.Add(Deserialize<Requirement>(mapping));
                    break;
                case GitOpsSchema.KindControl:
                    var control = Deserialize<Control>(mapping);
                    // Explicit-null list (e.g. `maps_to:`) deserializes to null; normalize
                    // to empty so the validator emits a diagnostic instead of throwing.
                    config.Controls.Add(control with { MapsTo = control.MapsTo ?? [] });
                    break;
                case GitOpsSchema.KindOrganisation:
                    config.Organisations.Add(Deserialize<Organisation>(mapping));
                    break;
                case GitOpsSchema.KindScope:
                    config.Scopes.Add(Deserialize<Scope>(mapping));
                    break;
                case GitOpsSchema.KindRequirementScope:
                    config.RequirementScopes.Add(Deserialize<RequirementScope>(mapping));
                    break;
            }
        }
        catch (YamlException ex)
        {
            diagnostics.Add(FromYamlException(relative, ex));
        }
    }

    private static T Deserialize<T>(YamlMappingNode mapping)
    {
        // Re-serialize the node and deserialize into the target type. Keeps the
        // representation-model parse (for kind/key-diff) and typed binding consistent.
        using var writer = new StringWriter();
        new YamlStream(new YamlDocument(mapping)).Save(writer, assignAnchors: false);
        var yaml = writer.ToString();
        using var reader = new StringReader(yaml);
        return Deserializer.Deserialize<T>(reader)!;
    }

    private static void ReportUnknownFields(
        YamlMappingNode mapping,
        string kind,
        HashSet<string> knownKeys,
        string relative,
        List<Diagnostic> diagnostics)
    {
        foreach (var entry in mapping.Children)
        {
            if (entry.Key is not YamlScalarNode key || key.Value is null)
            {
                continue;
            }

            if (!knownKeys.Contains(key.Value))
            {
                diagnostics.Add(new Diagnostic
                {
                    File = relative,
                    Line = (int)key.Start.Line,
                    Column = (int)key.Start.Column,
                    Message = $"Unknown field '{key.Value}' on {kind}.",
                });
            }
        }
    }

    private static string? ScalarValue(YamlMappingNode mapping, string key)
    {
        return mapping.Children
            .Where(entry => entry.Key is YamlScalarNode scalar && scalar.Value == key)
            .Select(entry => (entry.Value as YamlScalarNode)?.Value)
            .FirstOrDefault();
    }

    private static Diagnostic FromYamlException(string relative, YamlException ex)
    {
        var start = ex.Start;
        return new Diagnostic
        {
            File = relative,
            Line = start.Line > 0 ? (int)start.Line : null,
            Column = start.Column > 0 ? (int)start.Column : null,
            Message = $"Malformed YAML: {ex.Message}",
        };
    }
}
