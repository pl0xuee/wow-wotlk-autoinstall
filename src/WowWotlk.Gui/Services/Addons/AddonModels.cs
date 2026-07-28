using System.Text.Json.Serialization;

namespace WowWotlk.Gui.Services.Addons;

/// <summary>Where a catalog entry's zip comes from.</summary>
public enum AddonSourceKind
{
    /// <summary>An "owner/name" GitHub repo; the newest release supplies the zip.</summary>
    Github,

    /// <summary>A direct link to a zip.</summary>
    Url,
}

public sealed record AddonSource(AddonSourceKind Kind, string? Repo, string? Url);

/// <summary>One entry in the curated list shipped with the app.</summary>
public sealed record CatalogAddon(
    string Id,
    string Name,
    string Category,
    string Description,
    AddonSource Source,
    string? Homepage
);

/// <summary>
/// An addon this app put on disk. <paramref name="Source"/> is free-text provenance —
/// "catalog:questie", "zip", "url" — kept so the UI can tell a curated install from one the
/// user pasted in, without pretending the two are the same kind of thing.
/// </summary>
public sealed record InstalledAddon(
    string Id,
    string Name,
    IReadOnlyList<string> Folders,
    string? Version,
    string Source,
    DateTimeOffset InstalledAt
);

/// <summary>One directory found under Interface/AddOns (or AddOns.disabled), with whatever its .toc declared.</summary>
public sealed record AddonFolder(
    string Name,
    string Path,
    string? Title,
    string? Version,
    string? Interface,
    bool Enabled
)
{
    /// <summary>The interface number of the 3.3.5a client. Every addon written for WotLK declares it.</summary>
    public const string WotlkInterface = "30300";

    /// <summary>
    /// True when the .toc names an interface other than 3.3.5a's. This is a warning and never
    /// a reason to block an install: authors routinely leave the number stale after a patch,
    /// and the client loads a mismatched addon anyway once "Load out of date AddOns" is
    /// ticked on the character screen. It is shown because it is the first thing to check
    /// when an addon silently fails to appear.
    /// </summary>
    public bool InterfaceMismatch => Interface is not null && Interface != WotlkInterface;
}

[JsonSerializable(typeof(List<CatalogAddon>))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(JsonStringEnumConverter<AddonSourceKind>)]
)]
public partial class AddonCatalogCtx : JsonSerializerContext;

[JsonSerializable(typeof(List<InstalledAddon>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
public partial class InstalledAddonCtx : JsonSerializerContext;
