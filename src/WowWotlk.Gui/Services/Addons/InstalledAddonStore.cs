using System.Text.Json;

namespace WowWotlk.Gui.Services.Addons;

/// <summary>
/// installed-addons.json (~/.config/wow-wotlk-autoinstall/): what this app put under
/// Interface/AddOns, and above all which directories each addon owns.
///
/// The folder list is why the file exists. An addon's id has no relationship to the
/// directories its zip unpacks to — AtlasLoot lands eight, DBM thirty-odd, and a GitHub source
/// zip names them after the repo — so without the record, uninstall and update could only
/// guess. Guessing wrong either deletes a directory belonging to a different addon or leaves
/// half of this one behind, and the user finds out at the character screen.
/// </summary>
public class InstalledAddonStore
{
    public static string StorePath =>
        Path.Join(Models.AppSettings.AppDataPath, "installed-addons.json");

    public IReadOnlyList<InstalledAddon> All => _addons;

    /// <summary>Re-reads the file, discarding anything recorded in memory since the last save.</summary>
    public void Load() => _addons = ReadFile();

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(Models.AppSettings.AppDataPath);
        await AtomicFile.WriteAllTextAsync(
            StorePath,
            JsonSerializer.Serialize(_addons, InstalledAddonCtx.Default.ListInstalledAddon)
        );
    }

    public InstalledAddon? ById(string id) =>
        _addons.FirstOrDefault(a => a.Id.Equals(id, StringComparison.Ordinal));

    /// <summary>
    /// Records an install, and enforces the one invariant the rest of the app relies on: a
    /// folder under Interface/AddOns is claimed by at most one record.
    ///
    /// Two records can otherwise end up naming the same folder, because ids come from two
    /// unrelated namespaces — a catalog id ("dbm") for a curated install, a folder name
    /// ("DBM-Core") for a hand-installed zip. The UI indexes installed folders by name, so a
    /// duplicate claim is not a cosmetic problem: it throws while the page is being built,
    /// which happens during startup, and the record file persists — so the app stops opening
    /// at all until the file is deleted by hand.
    /// </summary>
    public void Record(InstalledAddon addon)
    {
        Forget(addon.Id);
        var claimed = addon.Folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _addons.RemoveAll(a => a.Folders.Any(claimed.Contains));
        _addons.Add(addon);
    }

    /// <summary>
    /// Drops the record with exactly this id. Ordinal on purpose: ids come from two namespaces
    /// that differ only in case for some addons ("questie" from the catalog, "Questie" from a
    /// folder), and an ignore-case match would delete the wrong one.
    /// </summary>
    public void Forget(string id) =>
        _addons.RemoveAll(a => a.Id.Equals(id, StringComparison.Ordinal));

    /// <summary>The record claiming <paramref name="folder"/>, or null.</summary>
    public InstalledAddon? ByFolder(string folder) =>
        _addons.FirstOrDefault(a =>
            a.Folders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase))
        );

    private static List<InstalledAddon> ReadFile()
    {
        if (!File.Exists(StorePath))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize(
                    File.ReadAllText(StorePath),
                    InstalledAddonCtx.Default.ListInstalledAddon
                ) ?? [];
        }
        catch (JsonException)
        {
            // Same bargain as AppSettings.Load(): keep the broken file for inspection instead
            // of overwriting it, and carry on with nothing recorded. The addons themselves are
            // still on disk, so the worst case is that the user removes them by hand.
            File.Copy(StorePath, StorePath + ".corrupt", true);
            return [];
        }
    }

    private List<InstalledAddon> _addons = ReadFile();
}
