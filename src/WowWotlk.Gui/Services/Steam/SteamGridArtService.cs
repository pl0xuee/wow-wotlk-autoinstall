using Avalonia.Platform;

namespace WowWotlk.Gui.Services.Steam;

/// <summary>
/// Installs the bundled shortcut artwork (Assets/SteamGrid, rendered by
/// scripts/gen-steam-grid.sh) into Steam's per-user grid directory so the WoW entry gets
/// library capsules, hero, logo and icon instead of gray placeholders.
/// </summary>
public class SteamGridArtService(LogService log)
{
    private const string AssetBase = "avares://WowWotlk.Gui/Assets/SteamGrid/";

    /// <summary>
    /// Writes the shortcut icon to a name that doesn't depend on the appid and returns its
    /// path, for shortcuts.vdf's icon field. Never throws: art is cosmetic and must not fail
    /// the setup pipeline.
    /// </summary>
    public string? InstallIcon(SteamInstallation steam)
    {
        try
        {
            var path = Path.Join(GridDir(steam), "wow-wotlk-icon.png");
            Copy("icon.png", path);
            return path;
        }
        catch (Exception e)
        {
            log.Append($"Could not write shortcut icon: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes capsules/hero/logo keyed by the shortcut's unsigned appid.
    ///
    /// Existing files are left alone. Steam's grid folder is also where SteamGridDB and Steam's
    /// own "Set custom artwork" put their images, so overwriting is how a user's chosen capsule
    /// disappears — and it would happen on every press of the Steam page's button.
    /// </summary>
    public void InstallGridArt(SteamInstallation steam, long unsignedAppId)
    {
        try
        {
            var grid = GridDir(steam);
            var wrote = 0;
            wrote += CopyIfAbsent("landscape.png", Path.Join(grid, $"{unsignedAppId}.png"));
            wrote += CopyIfAbsent("portrait.png", Path.Join(grid, $"{unsignedAppId}p.png"));
            wrote += CopyIfAbsent("hero.png", Path.Join(grid, $"{unsignedAppId}_hero.png"));
            wrote += CopyIfAbsent("logo.png", Path.Join(grid, $"{unsignedAppId}_logo.png"));
            log.Append(
                wrote == 4 ? $"Library artwork installed for appid {unsignedAppId}"
                : wrote == 0 ? $"Library artwork already set for appid {unsignedAppId}; left as it is"
                : $"Library artwork: wrote {wrote} of 4 images, leaving existing artwork in place"
            );
        }
        catch (Exception e)
        {
            log.Append($"Could not write library artwork: {e.Message}");
        }
    }

    private static string GridDir(SteamInstallation steam)
    {
        var dir = Path.Join(steam.UserConfigDir, "grid");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Copy(string asset, string destination)
    {
        using var source = AssetLoader.Open(new Uri(AssetBase + asset));
        using var dest = File.Create(destination);
        source.CopyTo(dest);
    }

    /// <summary>Writes the asset only if nothing is there; returns 1 if it wrote.</summary>
    private static int CopyIfAbsent(string asset, string destination)
    {
        if (File.Exists(destination))
        {
            return 0;
        }
        Copy(asset, destination);
        return 1;
    }
}
