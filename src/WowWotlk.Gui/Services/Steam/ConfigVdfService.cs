using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WowWotlk.Gui.Services.Steam;

/// <summary>
/// Sets the compatibility tool for an app by editing config/config.vdf as raw text
/// (Jackify NativeSteamService.set_proton_version port — text editing preserves Steam's
/// formatting). Key path: InstallConfigStore→Software→Valve→Steam→CompatToolMapping.
/// </summary>
public class ConfigVdfService
{
    public void SetCompatTool(SteamInstallation steam, long unsignedAppId, string toolName)
    {
        var path = steam.ConfigVdfPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Steam config.vdf not found at {path}");
        }
        Backup(path);
        var text = File.ReadAllText(path);

        var entry =
            $"\t\t\t\t\t\"{unsignedAppId}\"\n"
            + "\t\t\t\t\t{\n"
            + $"\t\t\t\t\t\t\"name\"\t\t\"{toolName}\"\n"
            + "\t\t\t\t\t\t\"config\"\t\t\"\"\n"
            + "\t\t\t\t\t\t\"priority\"\t\t\"250\"\n"
            + "\t\t\t\t\t}\n";

        if (FindBlockBody(text, "CompatToolMapping") is { } mapping)
        {
            // Everything below is confined to the mapping block. Steam keys several other
            // sections of config.vdf by the very same non-Steam appid — ShaderCacheManager→App
            // is one that exists on a stock install — so a file-wide search for the appid
            // deletes a block that has nothing to do with compatibility tools.
            var body = text[mapping.Start..mapping.End];
            var cleaned = RemoveAppIdBlock(body, unsignedAppId);
            text = text[..mapping.Start] + "\n" + entry + cleaned + text[mapping.End..];
        }
        else
        {
            // No CompatToolMapping yet — create it inside the "Steam" block.
            if (FindBlockBody(text, "Steam") is not { } steamBlock)
            {
                throw new InvalidDataException("No \"Steam\" block found in config.vdf");
            }
            var block = "\n\t\t\t\t\"CompatToolMapping\"\n\t\t\t\t{\n" + entry + "\t\t\t\t}\n";
            text = text.Insert(steamBlock.Start, block);
        }

        AtomicFile.WriteAllText(path, text);
    }

    /// <summary>Reads back the tool name mapped for an appid, for post-restart verification.</summary>
    public string? GetCompatTool(SteamInstallation steam, long unsignedAppId)
    {
        if (!File.Exists(steam.ConfigVdfPath))
        {
            return null;
        }
        var text = File.ReadAllText(steam.ConfigVdfPath);
        if (FindBlockBody(text, "CompatToolMapping") is not { } mapping)
        {
            return null;
        }
        // Scoped to the mapping block for the same reason as the write: an appid-keyed block in
        // an unrelated section would otherwise answer for this one, and a false "already
        // mapped" makes the caller skip writing the tool — leaving a shortcut Steam launches
        // with no Proton at all.
        var m = AppIdNameRx(unsignedAppId).Match(text[mapping.Start..mapping.End]);
        return m.Success ? m.Groups["name"].Value : null;
    }

    /// <summary>
    /// Drops the block keyed by <paramref name="unsignedAppId"/> from one VDF body, so a re-run
    /// or a tool change replaces its entry instead of stacking a second one.
    /// </summary>
    internal static string RemoveAppIdBlock(string body, long unsignedAppId) =>
        Regex.Replace(body, $"[ \\t]*\"{unsignedAppId}\"\\s*\\{{[^{{}}]*\\}}\\n?", "");

    /// <summary>
    /// The span between the braces of the named block, or null when there is no such block.
    ///
    /// Brace-matched rather than pattern-matched: the mapping block contains one nested block
    /// per app, so anything that stops at the first <c>}</c> stops in the wrong place. Quoted
    /// sections are skipped so a brace inside a value cannot unbalance the count.
    /// </summary>
    internal static (int Start, int End)? FindBlockBody(string text, string key)
    {
        // VDF keys are case-insensitive; real config.vdf files vary in casing.
        var keyIdx = text.IndexOf($"\"{key}\"", StringComparison.OrdinalIgnoreCase);
        if (keyIdx < 0)
        {
            return null;
        }
        var open = text.IndexOf('{', keyIdx);
        if (open < 0)
        {
            return null;
        }
        var depth = 0;
        var inQuotes = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && inQuotes)
            {
                i++;
                continue;
            }
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (inQuotes)
            {
                continue;
            }
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return (open + 1, i);
            }
        }
        return null;
    }

    private static Regex AppIdNameRx(long unsignedAppId) =>
        new(
            $"\"{unsignedAppId}\"\\s*\\{{[^{{}}]*\"name\"\\s+\"(?<name>[^\"]+)\"",
            RegexOptions.Singleline
        );

    private static void Backup(string path)
    {
        var backupDir = Path.Join(Path.GetDirectoryName(path), "backups");
        Directory.CreateDirectory(backupDir);
        File.Copy(
            path,
            Path.Join(backupDir, $"config_{DateTimeOffset.Now.ToUnixTimeSeconds()}.bak"),
            true
        );
        foreach (
            var stale in new DirectoryInfo(backupDir)
                .GetFiles("config_*.bak")
                .OrderByDescending(f => f.Name)
                .Skip(5)
        )
        {
            stale.Delete();
        }
    }
}
