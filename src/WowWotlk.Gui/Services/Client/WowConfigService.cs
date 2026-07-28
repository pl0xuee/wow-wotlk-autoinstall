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
/// </summary>
public partial class WowConfigService(LogService log)
{
    /// <summary>Applies the display settings and returns the file that was written.</summary>
    public string Apply(string clientRoot, DisplaySettings display)
    {
        var path = ConfigPath(clientRoot);
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var updated = SetValues(existing, Values(display));
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
    /// The directives that describe one display choice.
    ///
    /// gxMaximize goes with windowed mode: without it a windowed client at the desktop's own
    /// resolution opens with its title bar off-screen and cannot be moved back.
    /// </summary>
    internal static Dictionary<string, string> Values(DisplaySettings display) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gxResolution"] = display.Resolution.ToString(),
            ["gxWindow"] = display.Windowed ? "1" : "0",
            ["gxMaximize"] = display.Windowed ? "1" : "0",
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
