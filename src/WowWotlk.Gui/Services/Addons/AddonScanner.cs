namespace WowWotlk.Gui.Services.Addons;

/// <summary>
/// Lists what is actually sitting under Interface/AddOns and Interface/AddOns.disabled.
///
/// This is the truth the client goes by, and it is not the same list as
/// <see cref="InstalledAddonStore"/>: that records what this app installed, while a real
/// install also has whatever the user unzipped there by hand and whatever a repack shipped.
/// </summary>
public class AddonScanner
{
    public IReadOnlyList<AddonFolder> Scan(string clientRoot)
    {
        var found = ScanDir(Path.Join(clientRoot, "Interface", "AddOns"), enabled: true);
        found.AddRange(ScanDir(Path.Join(clientRoot, "Interface", "AddOns.disabled"), enabled: false));
        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    private static List<AddonFolder> ScanDir(string dir, bool enabled)
    {
        var found = new List<AddonFolder>();
        string[] children;
        try
        {
            children = Directory.Exists(dir) ? Directory.GetDirectories(dir) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return found;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            // Blizzard_* are the stock UI modules that ship inside the client — the auction
            // house window, the talent frame, the calendar. They live in the same directory but
            // are not the user's addons, and the thirty of them would bury the six that are.
            if (name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // A dot-prefixed folder is not something the client loads; here it is the wreckage
            // of an install that was interrupted between moving the old version aside and
            // putting the new one in. Listing it would offer the user a phantom addon.
            if (name.StartsWith('.'))
            {
                continue;
            }

            string? title = null;
            string? version = null;
            string? iface = null;
            if (AddonArchive.TocIn(child) is { } toc && ReadText(toc) is { } text)
            {
                (title, version, iface) = AddonArchive.ParseToc(text);
            }
            found.Add(new AddonFolder(name, child, title, version, iface, enabled));
        }
        return found;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
