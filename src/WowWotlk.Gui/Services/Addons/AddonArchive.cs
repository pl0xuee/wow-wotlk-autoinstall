using System.Text.RegularExpressions;

namespace WowWotlk.Gui.Services.Addons;

/// <summary>
/// Works out what an unpacked addon zip actually contains. Split out from the installer and
/// kept to directory listing and string parsing so the layout rules — the part that genuinely
/// varies between publishers, and the part that quietly installs the wrong folder when it is
/// wrong — can be tested against fixture trees.
/// </summary>
internal static partial class AddonArchive
{
    /// <summary>How many junk wrapper folders deep to look before giving up.</summary>
    private const int MaxDepth = 3;

    /// <summary>Absolute paths of the directories that belong under Interface/AddOns.</summary>
    internal static IReadOnlyList<string> FindAddonFolders(string extractedRoot) =>
        FindAddonFolders(extractedRoot, 0);

    private static IReadOnlyList<string> FindAddonFolders(string root, int depth)
    {
        string[] children;
        try
        {
            children = Directory.GetDirectories(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        Array.Sort(children, StringComparer.Ordinal);

        // Children carrying a .toc win over the single-child unwrap below, because until you
        // look for a .toc a multi-folder addon and a wrapper are the same shape. AtlasLoot
        // ships AtlasLoot/ beside AtlasLoot_Data/ and friends; unwrapping first would descend
        // into one of them and install a fraction of the addon.
        var addons = children.Where(HasToc).ToList();
        if (addons.Count > 0)
        {
            return addons;
        }
        if (HasToc(root))
        {
            return [root];
        }
        // A lone child and no .toc anywhere is the GitHub source-zip shape (Questie-master/),
        // so step through it.
        if (children.Length == 1 && depth < MaxDepth)
        {
            return FindAddonFolders(children[0], depth + 1);
        }
        return [];
    }

    internal static bool HasToc(string dir) => TocIn(dir) is not null;

    /// <summary>The .toc that describes <paramref name="dir"/>, or null.</summary>
    internal static string? TocIn(string dir)
    {
        List<string> tocs;
        try
        {
            // Matched by hand rather than with a "*.toc" search pattern: that pattern is
            // case-sensitive on Linux, and a zip built on Windows can carry a .TOC.
            tocs =
            [
                .. Directory
                    .EnumerateFiles(dir)
                    .Where(f => f.EndsWith(".toc", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.Ordinal),
            ];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        // A repo that supports several game flavours ships one .toc per flavour in the same
        // folder (Questie.toc, Questie-Classic.toc, Questie-335.toc). The client loads exactly
        // the one named after the folder, so that is the one whose title and version describe
        // what will actually run; anything else is a sibling build.
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        return tocs.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase)
            ) ?? tocs.FirstOrDefault();
    }

    /// <summary>
    /// Reads the Title/Version/Interface directives out of a .toc. The format is loose by
    /// convention: directive names are matched case-insensitively, spacing around the colon is
    /// down to the author, and the first occurrence wins so a localised block later in the
    /// file cannot overwrite the default title.
    /// </summary>
    internal static (string? Title, string? Version, string? Interface) ParseToc(string tocText)
    {
        string? title = null;
        string? version = null;
        string? iface = null;
        foreach (Match match in DirectiveRx().Matches(tocText))
        {
            var value = match.Groups["value"].Value.Trim();
            switch (match.Groups["key"].Value.ToLowerInvariant())
            {
                case "title":
                    title ??= StripColours(value);
                    break;
                case "version":
                    version ??= value;
                    break;
                case "interface":
                    iface ??= value;
                    break;
            }
        }
        return (Blank(title), Blank(version), Blank(iface));
    }

    /// <summary>
    /// Drops the client's inline colour escapes. Authors colour the title so it stands out in
    /// the in-game addon list (|cff00ff00Questie|r), and printed raw those eight hex digits
    /// read as corruption.
    /// </summary>
    internal static string StripColours(string value) => ColourRx().Replace(value, "").Trim();

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(
        @"^[ \t]*##[ \t]*(?<key>Title|Version|Interface)[ \t]*:(?<value>.*)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase
    )]
    private static partial Regex DirectiveRx();

    [GeneratedRegex(@"\|c[0-9a-fA-F]{8}|\|r")]
    private static partial Regex ColourRx();
}
