using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Microsoft.OpenApi.YamlReader;

namespace Freeboard.Web.Docs;

/// <summary>A named value in a parameter list or request body.</summary>
public sealed record ApiField(string Name, string Type, bool Required, string? Description);

public sealed record ApiResponse(string StatusCode, string Description, string? Example);

public sealed record ApiOperation(
    string Method,
    string Path,
    string Anchor,
    string? OperationId,
    string? Summary,
    string? Description,
    IReadOnlyList<ApiField> Parameters,
    IReadOnlyList<ApiField> RequestFields,
    string? RequestExample,
    IReadOnlyList<ApiResponse> Responses);

/// <summary>
/// The OpenAPI document behind the API reference pages: one page per tag, one block per
/// operation, in the order the document lists them. Read once at startup, so a spec that
/// does not parse fails the build rather than a visitor's request.
/// </summary>
public sealed class ApiReference
{
    private static readonly JsonSerializerOptions ExampleFormat = new() { WriteIndented = true };

    private readonly Dictionary<string, List<ApiOperation>> byTag;

    private ApiReference(
        string sourceFile,
        string version,
        string baseUrl,
        Dictionary<string, List<ApiOperation>> byTag)
    {
        SourceFile = sourceFile;
        Version = version;
        BaseUrl = baseUrl;
        this.byTag = byTag;
    }

    /// <summary>Filename of the spec, shown on the page so a reader knows what generated it.</summary>
    public string SourceFile { get; }

    public string Version { get; }

    public string BaseUrl { get; }

    public static async Task<ApiReference> LoadAsync(string contentRootPath)
    {
        var path = Path.Combine(contentRootPath, "Content", "openapi.yaml");

        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();

        await using var stream = File.OpenRead(path);
        var result = await OpenApiDocument.LoadAsync(stream, "yaml", settings);

        var errors = result.Diagnostic?.Errors ?? [];
        if (result.Document is null || errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"{path} is not a readable OpenAPI document: {string.Join("; ", errors.Select(e => e.Message))}");
        }

        var document = result.Document;
        var byTag = new Dictionary<string, List<ApiOperation>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (route, pathItem) in document.Paths)
        {
            foreach (var (method, operation) in pathItem.Operations ?? [])
            {
                var read = Read(method.Method, route, operation);
                foreach (var tag in operation.Tags ?? Enumerable.Empty<OpenApiTagReference>())
                {
                    if (!byTag.TryGetValue(tag.Name, out var operations))
                    {
                        byTag[tag.Name] = operations = [];
                    }

                    operations.Add(read);
                }
            }
        }

        return new ApiReference(
            Path.GetFileName(path),
            document.Info?.Version ?? "",
            document.Servers?.FirstOrDefault()?.Url ?? "",
            byTag);
    }

    /// <summary>Operations carrying the given tag, in document order. Empty when the tag is unknown.</summary>
    public IReadOnlyList<ApiOperation> Operations(string tag) =>
        byTag.TryGetValue(tag, out var operations) ? operations : [];

    public bool HasTag(string tag) => byTag.ContainsKey(tag);

    private static ApiOperation Read(string method, string route, OpenApiOperation operation)
    {
        var parameters = (operation.Parameters ?? [])
            .Select(p => new ApiField(p.Name ?? "", TypeName(p.Schema), p.Required, p.Description))
            .ToList();

        var body = operation.RequestBody?.Content?.FirstOrDefault().Value;

        return new ApiOperation(
            method.ToUpperInvariant(),
            route,
            Anchor(method, route, operation.OperationId),
            operation.OperationId,
            operation.Summary,
            operation.Description,
            parameters,
            Fields(body?.Schema),
            Example(body?.Example),
            [.. (operation.Responses ?? []).Select(r => new ApiResponse(
                r.Key,
                r.Value.Description ?? "",
                Example(r.Value.Content?.FirstOrDefault().Value?.Example)))]);
    }

    /// <summary>A stable in-page id, so the contents list on the right can link to the block.</summary>
    private static string Anchor(string method, string route, string? operationId) =>
        operationId is { Length: > 0 }
            ? operationId.ToLowerInvariant()
            : $"{method}-{route}".ToLowerInvariant().Replace('/', '-').Replace("{", "").Replace("}", "");

    /// <summary>Flattens a schema's own properties. Nested objects are left to the linked schema.</summary>
    private static IReadOnlyList<ApiField> Fields(IOpenApiSchema? schema)
    {
        if (schema?.Properties is not { Count: > 0 } properties)
        {
            return [];
        }

        var required = schema.Required ?? new HashSet<string>();
        return [.. properties.Select(p =>
            new ApiField(p.Key, TypeName(p.Value), required.Contains(p.Key), p.Value.Description))];
    }

    private static string TypeName(IOpenApiSchema? schema)
    {
        if (schema is null)
        {
            return "";
        }

        // A named component reads better than the "object" its schema resolves to.
        if (schema is OpenApiSchemaReference { Reference.Id: { Length: > 0 } id })
        {
            return id;
        }

        // A union with null (`type: [integer, 'null']`) reads better as the type plus a marker
        // than as the raw flags value.
        var nullable = schema.Type?.HasFlag(JsonSchemaType.Null) == true;
        var type = schema.Type is null
            ? "object"
            : string.Join(
                " | ",
                Enum.GetValues<JsonSchemaType>()
                    .Where(t => t != JsonSchemaType.Null && schema.Type.Value.HasFlag(t))
                    .Select(t => t.ToString().ToLowerInvariant()));

        if (type.Length == 0)
        {
            type = "object";
        }

        if (type == "array")
        {
            type = TypeName(schema.Items) is { Length: > 0 } item ? $"{item}[]" : "array";
        }

        return nullable ? $"{type} | null" : type;
    }

    private static string? Example(JsonNode? example) =>
        example is null ? null : example.ToJsonString(ExampleFormat);
}
