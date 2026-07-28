using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WowWotlk.Gui.Services.Steam;

public sealed record CompatTool(
    string InternalName,
    string DisplayName,
    string Directory,
    int? RequiredRuntimeAppId = null
)
{
    public string ProtonBinary => Path.Join(Directory, "proton");
    public override string ToString() => DisplayName;
}

/// <summary>
/// Scans compatibilitytools.d directories for installed Proton builds and ranks them for a
/// 3.3.5a client (see <see cref="WotlkProton"/>): newest GE-Proton first, then Valve Proton,
/// then everything else.
/// </summary>
public partial class CompatToolCatalog(IReadOnlyList<string>? wellKnownDirs = null)
{
    private static readonly string[] DefaultDirs =
    [
        Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam",
            "root",
            "compatibilitytools.d"
        ),
        "/usr/share/steam/compatibilitytools.d",
    ];

    private readonly IReadOnlyList<string> _wellKnownDirs = wellKnownDirs ?? DefaultDirs;

    public List<CompatTool> Scan(string? steamRoot)
    {
        List<string> dirs = [.. _wellKnownDirs];
        if (steamRoot is not null)
        {
            dirs.Add(Path.Join(steamRoot, "compatibilitytools.d"));
        }

        var tools = new List<CompatTool>();
        foreach (var dir in dirs.Distinct().Where(System.IO.Directory.Exists))
        {
            foreach (var toolDir in System.IO.Directory.GetDirectories(dir))
            {
                var vdf = Path.Join(toolDir, "compatibilitytool.vdf");
                if (!File.Exists(vdf) || !File.Exists(Path.Join(toolDir, "proton")))
                {
                    continue;
                }
                var text = File.ReadAllText(vdf);
                // First key inside "compat_tools" is the internal name Steam uses in CompatToolMapping.
                var nameMatch = InternalNameRx().Match(text);
                var displayMatch = DisplayNameRx().Match(text);
                if (!nameMatch.Success)
                {
                    continue;
                }
                tools.Add(
                    new CompatTool(
                        nameMatch.Groups["name"].Value,
                        displayMatch.Success ? displayMatch.Groups["dn"].Value : nameMatch.Groups["name"].Value,
                        toolDir,
                        ReadRequiredRuntimeAppId(toolDir)
                    )
                );
            }
        }
        tools.AddRange(ValveProtons(steamRoot).Where(v => !tools.Any(t => t.InternalName == v.InternalName)));
        return WotlkProton.Order(tools.DistinctBy(t => t.InternalName)).ToList();
    }

    /// <summary>
    /// Valve's own Proton builds, which Steam installs as ordinary apps under
    /// steamapps/common rather than into compatibilitytools.d.
    ///
    /// They carry a toolmanifest but no compatibilitytool.vdf, so a scan of only
    /// compatibilitytools.d finds nothing on a machine that has Proton Experimental and no
    /// GE build — and the Steam phase then refuses to run for want of a Proton, on a machine
    /// with a working one. Steam names these in CompatToolMapping by the app's folder-derived
    /// internal name, which is what the manifest directory is called.
    /// </summary>
    private static IEnumerable<CompatTool> ValveProtons(string? steamRoot)
    {
        if (steamRoot is null)
        {
            yield break;
        }
        foreach (var library in SteamLibraries.Enumerate(steamRoot))
        {
            var common = Path.Join(library, "steamapps", "common");
            if (!System.IO.Directory.Exists(common))
            {
                continue;
            }
            string[] candidates;
            try
            {
                candidates = System.IO.Directory.GetDirectories(common, "Proton*");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var toolDir in candidates)
            {
                if (!File.Exists(Path.Join(toolDir, "proton"))
                    || !File.Exists(Path.Join(toolDir, "toolmanifest.vdf")))
                {
                    continue;
                }
                var display = Path.GetFileName(toolDir);
                // "Proton - Experimental" is mapped as proton_experimental, "Proton 9.0" as
                // proton_9. Lowercased, spaces and dashes collapsed to underscores, and the
                // trailing ".0" dropped — that is the shape Steam writes.
                var internalName = ValveInternalName(display);
                yield return new CompatTool(
                    internalName,
                    display,
                    toolDir,
                    ReadRequiredRuntimeAppId(toolDir)
                );
            }
        }
    }

    internal static string ValveInternalName(string displayName)
    {
        var name = displayName.Replace(" - ", " ").Replace('-', ' ').Trim();
        if (name.EndsWith(".0", StringComparison.Ordinal))
        {
            name = name[..^2];
        }
        return string.Join('_', name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    /// <summary>
    /// The Steam Linux Runtime a build must run inside, from its toolmanifest. Anything
    /// unreadable or unrecognised reads as "no requirement": filtering out a build that works
    /// today would be a regression, while missing a requirement is only the status quo.
    /// </summary>
    private static int? ReadRequiredRuntimeAppId(string toolDir)
    {
        var manifest = Path.Join(toolDir, "toolmanifest.vdf");
        if (!File.Exists(manifest))
        {
            return null;
        }
        string text;
        try
        {
            text = File.ReadAllText(manifest);
        }
        catch (IOException)
        {
            return null;
        }
        var match = RequiredRuntimeRx().Match(text);
        return match.Success && int.TryParse(match.Groups["id"].Value, out var appId)
            ? appId
            : null;
    }

    [GeneratedRegex("\"compat_tools\"\\s*\\{\\s*\"(?<name>[^\"]+)\"", RegexOptions.Singleline)]
    private static partial Regex InternalNameRx();

    [GeneratedRegex("\"display_name\"\\s+\"(?<dn>[^\"]+)\"")]
    private static partial Regex DisplayNameRx();

    [GeneratedRegex("\"require_tool_appid\"\\s+\"(?<id>\\d+)\"")]
    private static partial Regex RequiredRuntimeRx();
}
