namespace WowWotlk.Gui.Services.Client;

/// <summary>
/// Finds the real client root — the folder holding Wow.exe.
///
/// It is never reliably the folder the user picked. The Drive zip may unpack flat or under a
/// "World of Warcraft 3.3.5a/" wrapper, and a folder someone points at by hand is as likely to
/// be the parent as the client itself. The name is matched case-insensitively because a zip
/// made on Windows and unpacked on Linux preserves whatever casing it was stored with.
/// </summary>
public static class ClientLocator
{
    public const string ExeName = "Wow.exe";

    /// <summary>How far below the starting folder to look. Deep enough for one wrapper folder plus slack.</summary>
    private const int MaxDepth = 3;

    /// <summary>Returns the directory containing Wow.exe, or null if there isn't one.</summary>
    public static string? Find(string? startDir)
    {
        if (string.IsNullOrWhiteSpace(startDir) || !Directory.Exists(startDir))
        {
            return null;
        }
        return Search(Path.GetFullPath(startDir), 0);
    }

    /// <summary>The full path to Wow.exe under <paramref name="startDir"/>, or null.</summary>
    public static string? FindExe(string? startDir) =>
        Find(startDir) is { } root ? ExePathIn(root) : null;

    /// <summary>The path Wow.exe actually has in <paramref name="clientRoot"/>, preserving its on-disk casing.</summary>
    public static string? ExePathIn(string clientRoot)
    {
        try
        {
            return Directory
                .EnumerateFiles(clientRoot)
                .FirstOrDefault(f =>
                    Path.GetFileName(f).Equals(ExeName, StringComparison.OrdinalIgnoreCase)
                );
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Search(string dir, int depth)
    {
        if (ExePathIn(dir) is not null)
        {
            return dir;
        }
        if (depth >= MaxDepth)
        {
            return null;
        }
        string[] children;
        try
        {
            children = Directory.GetDirectories(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        // Breadth-first by name so a deterministic answer comes back when a folder somehow
        // holds two clients — depth-first would depend on filesystem enumeration order.
        Array.Sort(children, StringComparer.Ordinal);
        foreach (var child in children)
        {
            if (Search(child, depth + 1) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
