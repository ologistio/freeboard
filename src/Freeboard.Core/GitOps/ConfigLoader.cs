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
    // The top-level `kind` is the document discriminator (Standard/Control/Asset/Scope). An Asset's
    // Company/Department/Vendor distinction is authored under `type` so it does not collide with the
    // discriminator.
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
            [GitOpsSchema.KindControl] = new(StringComparer.Ordinal) { "apiVersion", "kind", "id", "title", "maps_to", "evaluation" },
            [GitOpsSchema.KindAsset] = new(StringComparer.Ordinal)
            {
                "apiVersion", "kind", "id", "title", "type", "source", "parent", "owner", "tier", "data_classes",
            },
            [GitOpsSchema.KindScope] = new(StringComparer.Ordinal)
            {
                "apiVersion", "kind", "id", "title", "subject", "standard", "requirement", "control", "disposition", "justification",
            },
            // No `checks`, `body`, `fields`, `pass_mark`, or `quiz`: every type-specific payload is
            // authored inside `config`, so a half-migrated document carrying one at the top level fails
            // with an unknown-field diagnostic instead of being silently accepted as a second shape.
            [GitOpsSchema.KindCollector] = new(StringComparer.Ordinal)
            {
                "apiVersion", "kind", "id", "title", "control", "vendor", "type", "provider", "frequency",
                "threshold", "connection", "config",
            },
            [GitOpsSchema.KindIntegrationConnection] = new(StringComparer.Ordinal)
            {
                "apiVersion", "kind", "id", "title", "provider", "base_url", "discovery_cadence", "vendor",
            },
        };

    // Discovered-only keys: valid columns on a discovered Machine asset, but never authored in declared
    // config. Rejected by presence on the parsed key set (an omitted vs authored-blank field is
    // indistinguishable once deserialized), with a distinct message from the generic unknown-field one.
    private static readonly HashSet<string> DiscoveredOnlyAssetKeys = new(StringComparer.Ordinal)
    {
        "identity_kind", "identity_value", "state", "first_seen", "last_seen",
    };

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithAttributeOverride<Standard>(s => s.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        .WithAttributeOverride<Requirement>(r => r.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        .WithAttributeOverride<Control>(c => c.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        .WithAttributeOverride<Asset>(a => a.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        .WithAttributeOverride<Scope>(s => s.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        .WithAttributeOverride<Collector>(c => c.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
        .WithAttributeOverride<IntegrationConnection>(c => c.ApiVersion, new YamlMemberAttribute { Alias = "apiVersion", ApplyNamingConventions = false })
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
                Message = $"Unknown kind '{kind}'. Expected one of: {GitOpsSchema.KindStandard}, {GitOpsSchema.KindRequirement}, {GitOpsSchema.KindControl}, {GitOpsSchema.KindAsset}, {GitOpsSchema.KindScope}, {GitOpsSchema.KindCollector}, {GitOpsSchema.KindIntegrationConnection}.",
            });
            return;
        }

        ReportUnknownFields(mapping, kind, knownKeys, relative, diagnostics);

        if (string.Equals(kind, GitOpsSchema.KindCollector, StringComparison.Ordinal))
        {
            ReportUnknownConfigKeys(mapping, relative, diagnostics);
        }

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
                case GitOpsSchema.KindAsset:
                    var asset = Deserialize<Asset>(mapping);
                    // Explicit-null list (`data_classes:`) deserializes to null; normalize to empty so the
                    // validator and the import path read one shape. An absent key already binds to empty.
                    config.Assets.Add(asset with { DataClasses = asset.DataClasses ?? [] });
                    break;
                case GitOpsSchema.KindScope:
                    config.Scopes.Add(Deserialize<Scope>(mapping));
                    break;
                case GitOpsSchema.KindCollector:
                    var collector = Deserialize<Collector>(mapping);
                    // An explicit-null `config:` binds the whole member to null, overwriting the record
                    // default, so normalize the node ITSELF before touching anything inside it; the
                    // nested normalization below would otherwise dereference null and throw, breaking the
                    // never-throw contract for a document that binds cleanly and warrants no diagnostic.
                    var collectorConfig = collector.Config ?? new CollectorConfig();
                    // An explicit-null `fields:`/`quiz:`/`checks:`/`options:` binds that list to null;
                    // normalize to empty. A null SEQUENCE ITEM is treated differently on purpose: a null
                    // `checks` item is KEPT as an empty Check so the validator reports its missing
                    // source_key/name/severity, while a null `fields` or `quiz` item is DROPPED. Keeping a
                    // null form item would fail a document that validates today; dropping a null check
                    // would silently swallow the malformed-check diagnostic.
                    config.Collectors.Add(collector with
                    {
                        Config = collectorConfig with
                        {
                            Fields = (collectorConfig.Fields ?? []).Where(f => f is not null).Select(f => f with { Options = f.Options ?? [] }).ToList(),
                            Quiz = (collectorConfig.Quiz ?? []).Where(q => q is not null).Select(q => q with { Options = q.Options ?? [] }).ToList(),
                            Checks = (collectorConfig.Checks ?? []).Select(c => c ?? new Check()).ToList(),
                        },
                    });
                    break;
                case GitOpsSchema.KindIntegrationConnection:
                    config.IntegrationConnections.Add(Deserialize<IntegrationConnection>(mapping));
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

            if (knownKeys.Contains(key.Value))
            {
                continue;
            }

            // A discovered-only field on a declared Asset gets a distinct message: it is a real column
            // on a discovered Machine but is written by ingest, never authored in config.
            var isDiscoveredOnly = string.Equals(kind, GitOpsSchema.KindAsset, StringComparison.Ordinal)
                && DiscoveredOnlyAssetKeys.Contains(key.Value);
            diagnostics.Add(new Diagnostic
            {
                File = relative,
                Line = (int)key.Start.Line,
                Column = (int)key.Start.Column,
                Message = isDiscoveredOnly
                    ? $"Field '{key.Value}' on {kind} is discovered-only and cannot be authored in config."
                    : $"Unknown field '{key.Value}' on {kind}.",
            });
        }
    }

    // Unregistered-key rejection is evaluated on the authored mapping rather than on the parsed config,
    // because a key with an empty value parses to the same member as an absent one. So an unregistered
    // key is rejected whatever its value.
    private static void ReportUnknownConfigKeys(YamlMappingNode mapping, string relative, List<Diagnostic> diagnostics)
    {
        var schema = CollectorConfigSchema.For(ScalarValue(mapping, "type"), ScalarValue(mapping, "provider"));
        if (schema is null)
        {
            // A type or provider token that is unknown, or absent where the pair needs one, resolves no
            // schema. The validator names that token; one unknown-key diagnostic per authored key on top
            // of it would be a cascade from one mistake.
            return;
        }

        // A `config` that is not a mapping has no keys to diff. A scalar or a sequence fails the typed
        // bind, which is the one diagnostic the author gets; an explicit null binds cleanly to an absent
        // config and correctly yields none at all.
        if (ValueNode(mapping, "config") is not YamlMappingNode configMapping)
        {
            return;
        }

        foreach (var entry in configMapping.Children)
        {
            if (entry.Key is not YamlScalarNode key || key.Value is null
                || schema.Any(registered => string.Equals(registered.Name, key.Value, StringComparison.Ordinal)))
            {
                continue;
            }

            diagnostics.Add(new Diagnostic
            {
                File = relative,
                Line = (int)key.Start.Line,
                Column = (int)key.Start.Column,
                Message = $"Unknown field '{key.Value}' on {GitOpsSchema.KindCollector} config.",
            });
        }
    }

    private static YamlNode? ValueNode(YamlMappingNode mapping, string key)
    {
        return mapping.Children
            .Where(entry => entry.Key is YamlScalarNode scalar && scalar.Value == key)
            .Select(entry => entry.Value)
            .FirstOrDefault();
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
