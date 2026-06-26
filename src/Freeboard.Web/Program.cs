using System.Runtime.InteropServices;
using AspNetStatic;

NativeLibrary.SetDllImportResolver(
    typeof(System.Net.Quic.QuicListener).Assembly,
    (name, assembly, searchPath) => name == "msquic"
        ? throw new DllNotFoundException("HTTP/3 disabled: this app does not use QUIC.")
        : NativeLibrary.Load(name, assembly, searchPath));

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

builder.Services.AddSingleton<IStaticResourcesInfoProvider>(
    new StaticResourcesInfoProvider(
        [
            new PageResource("/legal/terms"),
        ]));

var app = builder.Build();

app.MapRazorPages();

// Generate the static site, then exit. Run with: dotnet run -- ssg-only
if (args.HasExitWhenDoneArg())
{
    var dest = Path.Combine(builder.Environment.ContentRootPath, "_site");
    Directory.CreateDirectory(dest);
    app.GenerateStaticContent(dest, exitWhenDone: true);
}

app.Run();
