namespace WowWotlk.Tests;

/// <summary>A scratch directory that deletes itself. Tests here write real files on purpose —
/// the behaviour under test is filesystem behaviour, and a mock would only re-assert the mock.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Join(System.IO.Path.GetTempPath(), "wowwotlk-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Join(params string[] parts) => System.IO.Path.Join([Path, .. parts]);

    /// <summary>Creates a file and every directory above it, and returns its full path.</summary>
    public string Write(string relativePath, string contents = "")
    {
        var full = Join(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leaked temp dir is not worth failing a green test run over.
        }
    }
}
