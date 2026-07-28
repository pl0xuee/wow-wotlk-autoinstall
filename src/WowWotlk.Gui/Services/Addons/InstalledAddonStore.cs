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
        _addons.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records an install, replacing any earlier record of the same id so a re-install does not duplicate.</summary>
    public void Record(InstalledAddon addon)
    {
        Forget(addon.Id);
        _addons.Add(addon);
    }

    public void Forget(string id) =>
        _addons.RemoveAll(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

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
