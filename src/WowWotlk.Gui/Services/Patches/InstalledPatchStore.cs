using System.Text.Json;

namespace WowWotlk.Gui.Services.Patches;

/// <summary>
/// installed-patches.json: which patch files this app put into a client, and where.
///
/// Kept for the same reason the addon store is — the exact path is not derivable after the
/// fact, because it depends on the client's locale and on the asset name at the time it was
/// installed, both of which can change.
/// </summary>
public class InstalledPatchStore
{
    public static string StorePath =>
        Path.Join(Models.AppSettings.AppDataPath, "installed-patches.json");

    public IReadOnlyList<InstalledPatch> All => _patches;

    public void Load() => _patches = ReadFile();

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(Models.AppSettings.AppDataPath);
        await AtomicFile.WriteAllTextAsync(
            StorePath,
            JsonSerializer.Serialize(_patches, InstalledPatchCtx.Default.ListInstalledPatch)
        );
    }

    public InstalledPatch? ById(string id) =>
        _patches.FirstOrDefault(p => p.Id.Equals(id, StringComparison.Ordinal));

    public void Record(InstalledPatch patch)
    {
        Forget(patch.Id);
        // One file can only belong to one patch, so a second claim on it replaces the first.
        _patches.RemoveAll(p =>
            p.RelativePath.Equals(patch.RelativePath, StringComparison.OrdinalIgnoreCase)
        );
        _patches.Add(patch);
    }

    public void Forget(string id) =>
        _patches.RemoveAll(p => p.Id.Equals(id, StringComparison.Ordinal));

    private static List<InstalledPatch> ReadFile()
    {
        if (!File.Exists(StorePath))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize(
                    File.ReadAllText(StorePath),
                    InstalledPatchCtx.Default.ListInstalledPatch
                ) ?? [];
        }
        catch (JsonException)
        {
            File.Copy(StorePath, StorePath + ".corrupt", true);
            return [];
        }
    }

    private List<InstalledPatch> _patches = ReadFile();
}
