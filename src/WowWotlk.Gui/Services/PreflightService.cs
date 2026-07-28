using WowWotlk.Gui.Models;
using WowWotlk.Gui.Services.Client;
using WowWotlk.Gui.Services.Steam;

namespace WowWotlk.Gui.Services;

public enum CheckState
{
    Ok,
    Warn,
    Fail,
}

public sealed record PreflightCheck(string Name, CheckState State, string Detail);

/// <summary>
/// Fail-before-the-download-starts checks, shown as a checklist on the Install page.
/// A 3.3.5a client is ~16.5 GB zipped and ~25 GB unpacked, and both copies exist at once
/// until the extract finishes.
/// </summary>
public class PreflightService(
    SteamLocator steamLocator,
    CompatToolCatalog compatTools,
    SteamRuntimeCatalog steamRuntimes,
    SettingsService settingsService
)
{
    public const long RequiredDownloadBytes = 18L * 1024 * 1024 * 1024;
    public const long RequiredInstallBytes = 27L * 1024 * 1024 * 1024;

    public Task<List<PreflightCheck>> RunAsync(
        string installDir,
        string downloadDir,
        ClientSource source,
        CancellationToken ct = default
    )
    {
        var checks = new List<PreflightCheck>();
        var settings = settingsService.Settings;

        // These paths end up inside quoted Steam launch options and VDF strings, where
        // a double-quote, colon or newline corrupts the command Steam later runs.
        if ((PathProblem(installDir) ?? PathProblem(downloadDir)) is { } pathProblem)
        {
            checks.Add(new PreflightCheck("Folder names", CheckState.Fail, pathProblem));
        }

        checks.Add(SourceCheck(source, settings));

        var steam = steamLocator.Locate();
        checks.Add(
            steam is null
                ? new PreflightCheck(
                    "Steam",
                    CheckState.Fail,
                    "Native Steam not found (Flatpak/Snap Steam is not supported)."
                )
                : new PreflightCheck("Steam", CheckState.Ok, steam.Root)
        );

        checks.Add(
            ProtonCheck(
                compatTools.Scan(steam?.Root),
                steamRuntimes.AvailabilityFor(steam?.Root),
                settings.PreferredProtonInternalName
            )
        );

        // Nothing is downloaded or unpacked when the client is already on disk, so the space
        // budget for that source is zero on both counts.
        if (source != ClientSource.ExistingFolder)
        {
            checks.AddRange(
                SpaceChecks(
                    installDir,
                    downloadDir,
                    source == ClientSource.GoogleDrive ? RequiredDownloadBytes : 0,
                    RequiredInstallBytes
                )
            );
        }

        ct.ThrowIfCancellationRequested();
        return Task.FromResult(checks);
    }

    /// <summary>
    /// Whether the chosen client source is actually usable. A missing zip or a folder with no
    /// Wow.exe in it is the single most likely reason an install dies, and it costs nothing to
    /// say so before the run starts rather than after.
    /// </summary>
    internal static PreflightCheck SourceCheck(ClientSource source, AppSettings settings) =>
        source switch
        {
            ClientSource.LocalZip when string.IsNullOrWhiteSpace(settings.LocalZipPath) =>
                new PreflightCheck("Client source", CheckState.Fail, "No zip file chosen."),
            ClientSource.LocalZip when !File.Exists(settings.LocalZipPath) =>
                new PreflightCheck(
                    "Client source",
                    CheckState.Fail,
                    $"{settings.LocalZipPath} does not exist."
                ),
            ClientSource.LocalZip => new PreflightCheck(
                "Client source",
                CheckState.Ok,
                settings.LocalZipPath!
            ),
            ClientSource.ExistingFolder when string.IsNullOrWhiteSpace(settings.ExistingClientPath) =>
                new PreflightCheck("Client source", CheckState.Fail, "No client folder chosen."),
            ClientSource.ExistingFolder when ClientLocator.Find(settings.ExistingClientPath) is null =>
                new PreflightCheck(
                    "Client source",
                    CheckState.Fail,
                    $"No {ClientLocator.ExeName} found in {settings.ExistingClientPath} — "
                        + "pick the folder that holds it (or its parent)."
                ),
            ClientSource.ExistingFolder => new PreflightCheck(
                "Client source",
                CheckState.Ok,
                ClientLocator.Find(settings.ExistingClientPath)!
            ),
            _ when string.IsNullOrWhiteSpace(settings.DriveFileId) => new PreflightCheck(
                "Client source",
                CheckState.Fail,
                "No Google Drive file id set — check Settings."
            ),
            _ => new PreflightCheck(
                "Client source",
                CheckState.Ok,
                $"Google Drive · {GoogleDriveDownloader.ClientZipName}"
            ),
        };

    /// <summary>
    /// Any modern Proton runs 3.3.5a, so this only fails when there is nothing runnable at
    /// all. A build whose Steam Linux Runtime is missing does not count as runnable: Steam
    /// launches it and silently gets nowhere.
    /// </summary>
    internal static PreflightCheck ProtonCheck(
        List<CompatTool> tools,
        Func<int, bool> runtimeInstalled,
        string? pinned
    )
    {
        if (tools.Count == 0)
        {
            return new PreflightCheck("Proton", CheckState.Fail, WotlkProton.Guidance);
        }
        var selection = WotlkProton.Select(tools, runtimeInstalled, pinned);
        if (selection.Tool is null)
        {
            var missing = selection.SubstitutedFor;
            return new PreflightCheck(
                "Proton",
                CheckState.Fail,
                missing is not null
                    ? $"{missing.DisplayName} {WotlkProton.SubstitutionReason(missing, runtimeInstalled)}, "
                        + "and no other build is usable. " + WotlkProton.Guidance
                    : WotlkProton.Guidance
            );
        }
        var detail = $"{selection.Tool.DisplayName} — {WotlkProton.Describe(selection.Suitability)}";
        return selection.SubstitutedFor is { } replaced
            ? new PreflightCheck(
                "Proton",
                CheckState.Warn,
                $"{detail}. Using it instead of {replaced.DisplayName}, which "
                    + WotlkProton.SubstitutionReason(replaced, runtimeInstalled) + "."
            )
            : new PreflightCheck("Proton", CheckState.Ok, detail);
    }

    /// <summary>
    /// Space checks. When both folders live on the same filesystem their requirements add up,
    /// so they must be checked together — two independent checks can each pass while the
    /// install still runs the disk dry partway through.
    /// </summary>
    internal static List<PreflightCheck> SpaceChecks(
        string installDir,
        string downloadDir,
        long requiredDownload,
        long requiredInstall
    )
    {
        try
        {
            var installVolume = VolumeFor(installDir);
            var downloadVolume = VolumeFor(downloadDir);
            if (installVolume is null || downloadVolume is null)
            {
                return
                [
                    new PreflightCheck(
                        "Disk space",
                        CheckState.Warn,
                        "Could not determine which drive these folders are on."
                    ),
                ];
            }
            if (
                requiredDownload == 0
                || string.Equals(
                    installVolume.RootDirectory.FullName,
                    downloadVolume.RootDirectory.FullName,
                    StringComparison.Ordinal
                )
            )
            {
                var volume = requiredDownload == 0 ? installVolume : downloadVolume;
                var where = requiredDownload == 0
                    ? volume.RootDirectory.FullName
                    : $"{volume.RootDirectory.FullName} (download and install share it)";
                return
                [
                    BuildSpaceCheck(
                        "Disk space",
                        volume.AvailableFreeSpace,
                        requiredDownload + requiredInstall,
                        where,
                        // One budget covers both folders here, so either one already being on
                        // disk means the requirement overstates what the run actually needs.
                        HasExistingInstall(installDir) || HasExistingDownloads(downloadDir)
                    ),
                ];
            }
            return
            [
                BuildSpaceCheck(
                    "Download space",
                    downloadVolume.AvailableFreeSpace,
                    requiredDownload,
                    downloadVolume.RootDirectory.FullName,
                    HasExistingDownloads(downloadDir)
                ),
                BuildSpaceCheck(
                    "Install space",
                    installVolume.AvailableFreeSpace,
                    requiredInstall,
                    installVolume.RootDirectory.FullName,
                    HasExistingInstall(installDir)
                ),
            ];
        }
        catch (Exception e)
        {
            return [new PreflightCheck("Disk space", CheckState.Warn, $"Could not check: {e.Message}")];
        }
    }

    /// <summary>
    /// The mounted filesystem holding <paramref name="path"/>. DriveInfo(path) just echoes the
    /// path back on Unix, so identify the volume by longest mount-point prefix instead —
    /// otherwise two folders on one disk look like two independent budgets.
    /// </summary>
    private static DriveInfo? VolumeFor(string path)
    {
        var full = Path.GetFullPath(path);
        DriveInfo? best = null;
        var bestLength = -1;
        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            try
            {
                root = drive.RootDirectory.FullName;
                _ = drive.AvailableFreeSpace; // skip pseudo/unreadable filesystems
            }
            catch (Exception)
            {
                continue;
            }
            if (!IsUnder(full, root) || root.Length <= bestLength)
            {
                continue;
            }
            best = drive;
            bestLength = root.Length;
        }
        return best;
    }

    /// <summary>Reason a folder path can't be used safely in launch options/VDF, or null if fine.</summary>
    internal static string? PathProblem(string dir)
    {
        foreach (var c in dir)
        {
            if (c == '"' || c == ':' || char.IsControl(c))
            {
                var shown = c == '"' ? "a double quote" : c == ':' ? "a colon" : "a control character";
                return $"'{dir}' contains {shown} — Steam launch options can't represent that; pick a simpler folder name.";
            }
        }
        return null;
    }

    /// <summary>Prefix match on directory boundaries, so /mnt/Games2 isn't "under" /mnt/Games.</summary>
    internal static bool IsUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Headroom a re-run still needs even though the client is already on disk. Below this it
    /// genuinely cannot write and should stop.
    /// </summary>
    public const long UpdateHeadroomBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Whether a client is already installed here — one cheap lookup, no tree walk.</summary>
    internal static bool HasExistingInstall(string installDir) =>
        ClientLocator.Find(installDir) is not null;

    internal static bool HasExistingDownloads(string downloadDir)
    {
        try
        {
            return Directory.EnumerateFiles(downloadDir).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static PreflightCheck BuildSpaceCheck(
        string name,
        long free,
        long required,
        string where,
        bool existingInstall = false
    )
    {
        var freeGb = free / (1024.0 * 1024 * 1024);
        var requiredGb = required / (1024.0 * 1024 * 1024);
        if (free >= required)
        {
            return new PreflightCheck(name, CheckState.Ok, $"{freeGb:F0} GB free on {where}");
        }
        // The requirement describes a fresh install. Re-running over an existing one reuses
        // what is already there, so demanding room for a second copy would block the re-run
        // and push the user into deleting the install they are trying to repair.
        if (existingInstall && free >= UpdateHeadroomBytes)
        {
            return new PreflightCheck(
                name,
                CheckState.Warn,
                $"{freeGb:F0} GB free on {where}, below the {requiredGb:F0} GB a fresh install "
                    + "needs — continuing anyway because a client is already there"
            );
        }
        return new PreflightCheck(
            name,
            CheckState.Fail,
            $"{freeGb:F0} GB free on {where}, {requiredGb:F0} GB needed — "
                + "point one of the folders at another drive, or free up space"
        );
    }
}
