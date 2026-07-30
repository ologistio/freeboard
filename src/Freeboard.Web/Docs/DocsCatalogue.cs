using System.Text.Json;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Freeboard.Web.Docs;

/// <summary>One block of the documentation rail.</summary>
/// <param name="Template">Which page shell renders the group: "guide" or "legal".</param>
public sealed record DocsGroup(string Label, string Template, IReadOnlyList<DocsItem> Items);

/// <summary>The dated header a legal document carries above its body.</summary>
public sealed record DocsLegalMeta(string Effective, string Version, string AppliesTo);

/// <param name="Slug">Path under Content/Docs, without the .md suffix. Also the URL.</param>
/// <param name="Label">Rail label, kept short. The page heading comes from the body's H1.</param>
/// <param name="Tag">Badge shown beside the rail label, e.g. NEW.</param>
/// <param name="ApiTag">
/// An OpenAPI tag. When set, the operations carrying that tag are rendered under the
/// page's markdown, which is then an introduction rather than the whole page.
/// </param>
public sealed record DocsItem(
    string Slug,
    string Label,
    string? Lede = null,
    string? Tag = null,
    string? Reviewed = null,
    string? ApiTag = null,
    DocsLegalMeta? Legal = null);

public sealed record DocsEntry(DocsItem Item, DocsGroup Group)
{
    public string Url => DocsCatalogue.UrlFor(Item.Slug);
}

public sealed record DocsHeading(string Id, string Text);

public sealed record DocsBody(string Title, string Html, IReadOnlyList<DocsHeading> Headings);

/// <summary>
/// The documentation set: Content/Docs/docs.json orders the rail and carries each page's
/// metadata, and one markdown file per entry holds the body. Loaded once at startup so a
/// missing body fails the build rather than a visitor's request.
/// </summary>
public sealed class DocsCatalogue
{
    /// <summary>The rail's first page also answers /docs, so its slug stays out of the URL.</summary>
    public const string IndexSlug = "index";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <remarks>
    /// Deliberately not UseAdvancedExtensions: that turns on SmartyPants, which rewrites
    /// straight quotes and hyphens into typographic Unicode.
    /// </remarks>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    private readonly string contentDir;
    private readonly List<DocsEntry> pages;
    private readonly Dictionary<string, DocsEntry> bySlug;

    private DocsCatalogue(string contentDir, IReadOnlyList<DocsGroup> groups)
    {
        this.contentDir = contentDir;
        Groups = groups;
        pages = [.. groups.SelectMany(g => g.Items.Select(i => new DocsEntry(i, g)))];
        bySlug = pages.ToDictionary(p => p.Item.Slug, StringComparer.Ordinal);
    }

    public IReadOnlyList<DocsGroup> Groups { get; }

    /// <summary>Every page in rail order, which is also previous/next order.</summary>
    public IReadOnlyList<DocsEntry> Pages => pages;

    public static string UrlFor(string slug) => slug == IndexSlug ? "/docs" : $"/docs/{slug}";

    public static DocsCatalogue Load(string contentRootPath)
    {
        var dir = Path.Combine(contentRootPath, "Content", "Docs");
        var manifest = Path.Combine(dir, "docs.json");
        var groups = JsonSerializer.Deserialize<List<DocsGroup>>(File.ReadAllText(manifest), JsonOptions)
            ?? throw new InvalidOperationException($"{manifest} holds no documentation groups.");

        var catalogue = new DocsCatalogue(dir, groups);

        var missing = catalogue.pages
            .Where(p => !File.Exists(catalogue.BodyPath(p.Item.Slug)))
            .Select(p => p.Item.Slug)
            .ToList();
        if (missing.Count > 0)
        {
            throw new FileNotFoundException(
                $"{manifest} lists documentation pages with no markdown body: {string.Join(", ", missing)}");
        }

        return catalogue;
    }

    /// <summary>Resolves a URL slug. Only slugs listed in the manifest can reach the filesystem.</summary>
    public DocsEntry? Find(string? slug) =>
        bySlug.GetValueOrDefault(string.IsNullOrWhiteSpace(slug) ? IndexSlug : slug.Trim('/'));

    /// <summary>The adjacent page in the same group. Reading order does not run across groups.</summary>
    public DocsEntry? Neighbour(DocsEntry entry, int offset)
    {
        var index = pages.IndexOf(entry) + offset;
        if (index < 0 || index >= pages.Count)
        {
            return null;
        }

        var neighbour = pages[index];
        return ReferenceEquals(neighbour.Group, entry.Group) ? neighbour : null;
    }

    public DocsBody Read(DocsEntry entry)
    {
        var document = Markdown.Parse(File.ReadAllText(BodyPath(entry.Item.Slug)), Pipeline);

        // The H1 is lifted out of the body: each template places the page heading itself,
        // and the file keeps it so the markdown reads as a whole document on its own.
        var title = entry.Item.Label;
        if (document.FirstOrDefault() is HeadingBlock { Level: 1 } h1)
        {
            title = HeadingText(h1);
            document.Remove(h1);
        }

        var headings = document.Descendants<HeadingBlock>()
            .Where(h => h.Level == 2)
            .Select(h => new DocsHeading(h.GetAttributes().Id ?? string.Empty, HeadingText(h)))
            .Where(h => h.Id.Length > 0)
            .ToList();

        return new DocsBody(title, document.ToHtml(Pipeline), headings);
    }

    private string BodyPath(string slug) =>
        Path.Combine(contentDir, slug.Replace('/', Path.DirectorySeparatorChar) + ".md");

    private static string HeadingText(HeadingBlock heading) =>
        heading.Inline is null
            ? string.Empty
            : string.Concat(heading.Inline.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
}
