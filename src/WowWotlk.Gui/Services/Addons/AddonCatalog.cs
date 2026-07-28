using System.Text.Json;
using Avalonia.Platform;

namespace WowWotlk.Gui.Services.Addons;

/// <summary>
/// The curated addon list, baked into the app as an Avalonia resource rather than fetched.
/// A 3.3.5a client is offline software; the browse list has to work on a machine that has
/// only ever seen the game zip.
/// </summary>
public class AddonCatalog(LogService log)
{
    private const string AssetUri = "avares://WowWotlk.Gui/Assets/addon-catalog.json";

    public IReadOnlyList<CatalogAddon> Entries => _entries ??= Read();

    public CatalogAddon? ById(string id) =>
        Entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<CatalogAddon> Read()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(AssetUri));
            return JsonSerializer.Deserialize(stream, AddonCatalogCtx.Default.ListCatalogAddon)
                ?? [];
        }
        catch (Exception e)
        {
            // Deliberately broad: a catalog that fails to load costs the user the browse list
            // and nothing else — installing from a zip or a URL still works — so no shape of
            // failure here is worth taking the Addons page down for.
            log.Append($"Could not read the bundled addon catalog: {e.Message}");
            return [];
        }
    }

    private IReadOnlyList<CatalogAddon>? _entries;
}
