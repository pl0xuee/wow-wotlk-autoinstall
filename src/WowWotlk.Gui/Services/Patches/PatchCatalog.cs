using System.Text.Json;
using Avalonia.Platform;

namespace WowWotlk.Gui.Services.Patches;

/// <summary>The curated patch list, baked into the app for the same reason the addon one is.</summary>
public class PatchCatalog(LogService log)
{
    private const string AssetUri = "avares://WowWotlk.Gui/Assets/patch-catalog.json";

    public IReadOnlyList<ClientPatch> Entries => _entries ??= Read();

    public ClientPatch? ById(string id) =>
        Entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses catalog JSON, so the shipped file can be checked without Avalonia.</summary>
    internal static List<ClientPatch> Parse(string json) =>
        JsonSerializer.Deserialize(json, PatchCatalogCtx.Default.ListClientPatch) ?? [];

    private IReadOnlyList<ClientPatch> Read()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(AssetUri));
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception e)
        {
            // Broad on purpose: a catalog that fails to load costs the browse list and nothing
            // else, so no shape of failure here is worth taking a page down for.
            log.Append($"Could not read the bundled patch catalog: {e.Message}");
            return [];
        }
    }

    private IReadOnlyList<ClientPatch>? _entries;
}
