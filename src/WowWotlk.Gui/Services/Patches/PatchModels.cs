using System.Text.Json.Serialization;

namespace WowWotlk.Gui.Services.Patches;

/// <summary>
/// One MPQ patch the app can install into a client.
///
/// Distinct from an addon in every way that matters: it is a single file rather than a folder
/// tree, it lives in Data/&lt;locale&gt; rather than Interface/AddOns, it is loaded by the game
/// engine rather than the Lua interface, and which file you need depends on the client's
/// language. Sharing the addon machinery would mean lying about all four.
/// </summary>
public sealed record ClientPatch(
    string Id,
    string Name,
    string Description,
    /// <summary>"owner/name" — the release its file comes from.</summary>
    string Repo,
    /// <summary>Release asset name, with {locale} standing in for the client's language.</summary>
    string Asset,
    string? Homepage,
    /// <summary>Ticked by default in the one-click install.</summary>
    bool Recommended = false
)
{
    public string AssetFor(string locale) => Asset.Replace("{locale}", locale, StringComparison.Ordinal);
}

/// <summary>A patch this app put into a client, and exactly which file, so removing it is exact.</summary>
public sealed record InstalledPatch(
    string Id,
    string Name,
    string Locale,
    /// <summary>Path relative to the client root, so a moved client does not orphan the record.</summary>
    string RelativePath,
    string? Version,
    DateTimeOffset InstalledAt
);

[JsonSerializable(typeof(List<ClientPatch>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
public partial class PatchCatalogCtx : JsonSerializerContext;

[JsonSerializable(typeof(List<InstalledPatch>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
public partial class InstalledPatchCtx : JsonSerializerContext;
