namespace Freeboard.Core.Tests;

/// <summary>
/// Creates a throwaway directory of YAML files for a test and deletes it on dispose.
/// </summary>
public sealed class TempConfig : IDisposable
{
    public string Path { get; }

    private TempConfig(string path) => Path = path;

    public static TempConfig Create(params (string Name, string Content)[] files)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fb-gitops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(System.IO.Path.Combine(dir, name), content);
        }

        return new TempConfig(dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
