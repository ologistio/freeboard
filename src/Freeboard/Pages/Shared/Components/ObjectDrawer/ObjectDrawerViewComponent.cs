using Microsoft.AspNetCore.Mvc;

namespace Freeboard.Pages.Shared.Components.ObjectDrawer;

/// <summary>
/// The single object-detail drawer shell (O4/A5): the scrim, the <c>role="dialog"</c> panel, its close
/// button, and an empty content slot. It is mounted once in <c>_Layout</c> as a sibling of the rail and
/// stage; a list wires its rows to open it through the <c>drawer</c> store, and the record markup is
/// cloned in from the row's server-rendered template on open. It holds no per-record state, so the view
/// takes no model.
/// </summary>
public sealed class ObjectDrawerViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => View();
}
