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
        return Search(Path.GetFullPath(startDir), MaxDepth);
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

    /// <summary>
    /// Breadth-first, and by name within a level.
    ///
    /// Both parts matter. Shallowest-first means a real client at <c>&lt;dir&gt;/WoW/Wow.exe</c>
    /// wins over an old copy at <c>&lt;dir&gt;/Backup/Old/Wow.exe</c>, which a depth-first walk
    /// would reach first purely because "Backup" sorts earlier — and the install then writes
    /// the realmlist into the backup and deletes its Cache. Sorting by name makes the answer
    /// the same on every run rather than depending on filesystem enumeration order.
    /// </summary>
    private static string? Search(string root, int maxDepth)
    {
        var level = new List<string> { root };
        for (var depth = 0; depth <= maxDepth && level.Count > 0; depth++)
        {
            foreach (var dir in level)
            {
                if (ExePathIn(dir) is not null)
                {
                    return dir;
                }
            }
            if (depth == maxDepth)
            {
                break;
            }
            var next = new List<string>();
            foreach (var dir in level)
            {
                try
                {
                    next.AddRange(Directory.GetDirectories(dir));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // An unreadable folder simply contributes nothing to the next level.
                }
            }
            next.Sort(StringComparer.Ordinal);
            level = next;
        }
        return null;
    }
}
