using Freeboard.Web.Docs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Web.Pages;

/// <summary>Renders every documentation page; the group's template picks the shell.</summary>
public sealed class DocsPageModel(DocsCatalogue catalogue) : PageModel
{
    public DocsEntry Entry { get; private set; } = null!;
    public DocsBody Body { get; private set; } = null!;
    public DocsEntry? Previous { get; private set; }
    public DocsEntry? Next { get; private set; }

    public IActionResult OnGet(string? slug)
    {
        var entry = catalogue.Find(slug);
        if (entry is null)
        {
            return NotFound();
        }

        Entry = entry;
        Body = catalogue.Read(entry);
        Previous = catalogue.Neighbour(entry, -1);
        Next = catalogue.Neighbour(entry, 1);
        return Page();
    }
}
