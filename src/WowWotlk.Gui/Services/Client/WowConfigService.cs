using System.Text.RegularExpressions;
using WowWotlk.Gui.Services.Display;

namespace WowWotlk.Gui.Services.Client;

/// <summary>How the client should present itself on screen.</summary>
public sealed record DisplaySettings(DisplayMode Resolution, bool Windowed);

/// <summary>
/// Reads and writes WTF/Config.wtf, the client's own settings file.
///
/// Written before the game has ever run, which is the whole point: a 3.3.5a client with no
/// Config.wtf starts at 800x600 windowed on the first launch, and changing it from the in-game
/// menu means finding a menu rendered at 800x600 first. The client creates the file itself on
/// first exit, but it reads one that is already there — so writing it up front is both safe
/// and the only way to get a sensible first launch.
///
/// Every directive the file already has is preserved. Config.wtf accumulates everything the
/// user has ever changed in the options menu, so rewriting it wholesale to set two values
/// would silently discard their sound, camera and combat-text preferences.
///
/// Writing the resolution obliges us to write the graphics settings as well. The resolution
/// only survives if the client's first-launch hardware detection is suppressed, and that same
/// detection is what would otherwise have chosen the quality settings — so suppressing it
/// without furnishing them would trade a first launch at the wrong resolution for one at the
/// client's built-in defaults. Only for a config nobody has played: see <see cref="Values"/>.
/// </summary>
public partial class WowConfigService(LogService log)
{
    /// <summary>Applies the display settings and returns the file that was written.</summary>
    public string Apply(string clientRoot, DisplaySettings display)
    {
        var path = ConfigPath(clientRoot);
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var updated = SetValues(existing, Values(display, HasRunBefore(existing)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, updated);
        log.Append(
            $"Display set to {display.Resolution} "
                + $"{(display.Windowed ? "windowed" : "fullscreen")} in {path}"
        );
        return path;
    }

    /// <summary>What the client is currently set to, or null when it has no config yet.</summary>
    public DisplaySettings? Read(string clientRoot)
    {
        var path = ConfigPath(clientRoot);
        if (!File.Exists(path))
        {
            return null;
        }
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (ReadValue(text, "gxResolution") is not { } resolution
            || ParseMode(resolution) is not { } mode)
        {
            return null;
        }
        // gxWindow is "1" for windowed. A file that never set it has been fullscreen.
        var windowed = ReadValue(text, "gxWindow") == "1";
        return new DisplaySettings(mode, windowed);
    }

    public static string ConfigPath(string clientRoot) =>
        Path.Join(clientRoot, "WTF", "Config.wtf");

    /// <summary>
    /// Whether the client has already completed a launch.
    ///
    /// The client writes hwDetect "0" itself once its hardware detection has run, so the
    /// directive's presence is the one durable record that someone has played this install —
    /// which is what separates a config we may furnish with defaults from one that is the
    /// user's own work.
    /// </summary>
    internal static bool HasRunBefore(string existing) => ReadValue(existing, "hwDetect") == "0";

    /// <summary>
    /// The directives one display choice implies.
    ///
    /// gxMaximize goes with windowed mode: without it a windowed client at the desktop's own
    /// resolution opens with its title bar off-screen and cannot be moved back.
    ///
    /// hwDetect "0" is what makes the other three survive. On a config that does not set it the
    /// client treats it as 1 and runs its first-launch hardware detection, which resets the
    /// whole video block — resolution included — before it ever sets a mode. gx.log from an
    /// install that had 3440x1440 written into it:
    ///
    ///     ConsoleDeviceInitialize(): hwDetect = 1, hwChanged = 0
    ///     CGxDeviceD3d::DeviceSetFormat(): Format 1024 x 768 @ 60 Fullscreen
    ///
    /// The resolution was read and then thrown away. With hwDetect "0" the same client on the
    /// same machine opens at 3440x1440 @ 240.
    /// </summary>
    internal static Dictionary<string, string> Values(DisplaySettings display, bool hasRunBefore)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gxResolution"] = display.Resolution.ToString(),
            ["gxWindow"] = display.Windowed ? "1" : "0",
            ["gxMaximize"] = display.Windowed ? "1" : "0",
            ["hwDetect"] = "0",
        };
        if (hasRunBefore)
        {
            // Someone has played this install, so the quality settings in the file are theirs.
            // Suppressing detection is still right — it would discard the resolution they just
            // picked — but furnishing defaults over the top of their own choices is not.
            return values;
        }
        foreach (var (key, value) in QualityPreset)
        {
            values[key] = value;
        }
        return values;
    }

    /// <summary>
    /// The client's own highest quality preset, for a config that has never been played.
    ///
    /// Needed because hwDetect "0" suppresses the detection that would otherwise choose these,
    /// so leaving them out trades a first launch at the wrong resolution for one at the client's
    /// built-in defaults. Any machine that can run this installer maxes a 2010 D3D9 game, so the
    /// top preset is the right one rather than a guess at the hardware.
    ///
    /// Values are the client's own, not a guess: GraphicsQualityLevels.lua in the client's
    /// Interface MPQ defines six quality levels, and level 5 is the one its author labelled
    /// "--ULTRA mode" (6 is the sentinel for a custom mix, not a higher preset). The control
    /// names there map to these cvars through VideoOptionsPanels.xml.
    ///
    /// TerrainDetail is the one level-5 entry with no directive here: its slider calls
    /// SetTerrainMip() rather than setting a cvar, so it is not a Config.wtf value at all.
    /// </summary>
    internal static readonly Dictionary<string, string> QualityPreset =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["farclip"] = "1277", // ViewDistance
            ["particleDensity"] = "1.0", // ParticleDensity
            ["extShadowQuality"] = "5", // ShadowQuality
            ["environmentDetail"] = "1.5", // EnvironmentDetail
            ["groundEffectDensity"] = "64", // ClutterDensity
            ["groundEffectDist"] = "140", // ClutterRadius
            ["BaseMip"] = "1", // TextureResolution
            ["textureFilteringMode"] = "5", // TextureFiltering
            ["weatherDensity"] = "3", // WeatherIntensity
            ["componentTextureLevel"] = "9", // PlayerTexture
            ["specular"] = "1", // SpecularLighting
            ["ffxGlow"] = "1", // FullScreenGlow
            ["ffxDeath"] = "1", // DeathEffect
            ["projectedTextures"] = "1", // ProjectedTextures
        };

    /// <summary>
    /// Rewrites the named SET directives, leaving every other line as it was.
    ///
    /// Exposed for tests: the failure this guards against is losing the rest of a config file
    /// that has accumulated a user's every option-menu change.
    /// </summary>
    internal static string SetValues(string existing, Dictionary<string, string> values)
    {
        var result = existing;
        foreach (var (key, value) in values)
        {
            var line = $"SET {key} \"{value}\"";
            var rx = DirectiveRx(key);
            if (rx.IsMatch(result))
            {
                // Every occurrence: the client reads the file top to bottom and the last
                // assignment wins, so replacing only the first leaves the old value in force.
                // A MatchEvaluator because $ is a substitution character in a replacement.
                result = rx.Replace(result, _ => line);
            }
            else
            {
                result = result.Length == 0 ? line + "\n"
                    : result.EndsWith('\n') ? result + line + "\n"
                    : result + "\n" + line + "\n";
            }
        }
        return result;
    }

    internal static string? ReadValue(string text, string key)
    {
        var match = DirectiveRx(key).Match(text);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    /// <summary>"1920x1080" → a mode, or null when it is not a resolution.</summary>
    internal static DisplayMode? ParseMode(string value)
    {
        var match = ResolutionRx().Match(value.Trim());
        return match.Success
            && int.TryParse(match.Groups["w"].Value, out var w)
            && int.TryParse(match.Groups["h"].Value, out var h)
            && w > 0
            && h > 0
            ? new DisplayMode(w, h)
            : null;
    }

    /// <summary>
    /// Matches one SET line. The client is case-insensitive about both the keyword and the key,
    /// and repacks vary in how they quote the value, so neither is assumed.
    /// </summary>
    private static Regex DirectiveRx(string key) =>
        new(
            $"^[ \\t]*SET[ \\t]+{Regex.Escape(key)}[ \\t]+\"?(?<value>[^\"\\r\\n]*)\"?[ \\t]*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase
        );

    [GeneratedRegex(@"^(?<w>\d{3,5})\s*[xX]\s*(?<h>\d{3,5})$")]
    private static partial Regex ResolutionRx();
}
