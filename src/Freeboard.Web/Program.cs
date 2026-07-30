using System.Runtime.InteropServices;
using AspNetStatic;
using Freeboard.Web.Docs;

NativeLibrary.SetDllImportResolver(
    typeof(System.Net.Quic.QuicListener).Assembly,
    (name, assembly, searchPath) => name == "msquic"
        ? throw new DllNotFoundException("HTTP/3 disabled: this app does not use QUIC.")
        : NativeLibrary.Load(name, assembly, searchPath));

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

var docs = DocsCatalogue.Load(builder.Environment.ContentRootPath);
var api = await ApiReference.LoadAsync(builder.Environment.ContentRootPath);
builder.Services.AddSingleton(docs);
builder.Services.AddSingleton(api);

// A page promising an OpenAPI tag the spec does not define would render an empty
// reference section, which reads as "no endpoints" rather than as the mistake it is.
var unknownTags = docs.Pages
    .Select(p => p.Item.ApiTag)
    .Where(t => t is not null && !api.HasTag(t))
    .ToList();
if (unknownTags.Count > 0)
{
    throw new InvalidOperationException(
        $"docs.json references OpenAPI tags that {api.SourceFile} does not define: {string.Join(", ", unknownTags)}");
}

builder.Services.AddSingleton<IStaticResourcesInfoProvider>(
    new StaticResourcesInfoProvider(
        [
            new PageResource("/"),
            // OutFile puts this at the output root as 404.html rather than 404/index.html,
            // which is where the host looks for a static site's not-found page.
            new PageResource("/404") { OutFile = "404.html" },
            .. docs.Pages.Select(p => new PageResource(p.Url)),
        ]));

var app = builder.Build();

app.UseStaticFiles();
app.MapRazorPages();

// Generate the static site, then exit. Run with: dotnet run -- ssg-only
if (args.HasExitWhenDoneArg())
{
    var dest = Path.Combine(builder.Environment.ContentRootPath, "_site");
    Directory.CreateDirectory(dest);

    // AspNetStatic writes only the routes it is given, so the built CSS/JS and the
    // fonts are carried across as files.
    CopyDirectory(builder.Environment.WebRootPath, dest);

    // alwaysDefaultFile puts every page at <route>/index.html, which any static host
    // resolves for the bare route; dontUpdateLinks then leaves hrefs as the clean
    // routes the templates wrote rather than rewriting them to .html paths.
    app.GenerateStaticContent(dest, exitWhenDone: true, alwaysDefaultFile: true, dontUpdateLinks: true);
}

app.Run();

static void CopyDirectory(string source, string destination)
{
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}
