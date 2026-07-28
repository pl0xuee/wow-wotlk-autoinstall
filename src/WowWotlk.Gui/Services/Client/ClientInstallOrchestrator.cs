using WowWotlk.Gui.Services.Addons;
using WowWotlk.Gui.Services.Patches;
using WowWotlk.Gui.Models;
using WowWotlk.Gui.Services.Steam;

namespace WowWotlk.Gui.Services.Client;

public enum InstallPhase
{
    Preflight,
    Acquire,
    Extract,
    Configure,
    Addons,
    SteamSetup,
    Done,
}

/// <summary>
/// A phase change plus, when the phase can report one, how far through it is. Extract and
/// Acquire both know their totals; Configure and SteamSetup do not, and say so with a null
/// fraction so the UI can animate rather than sit frozen at the last number it saw.
///
/// <paramref name="Counter"/> is the one machine value worth showing at size — bytes for a
/// download, percent for an extract. It is kept apart from <paramref name="Detail"/> because
/// the two are set in different typefaces: the counter is the thing a user watches for an
/// hour, the detail is the sentence underneath it.
/// </summary>
public sealed record InstallProgress(
    InstallPhase Phase,
    string Detail,
    double? Fraction,
    string Counter = ""
);

/// <summary>
/// The whole client install as one sequence: acquire → extract → point at the server →
/// optionally register with Steam.
///
/// Which of those actually run depends on the source. A client already on disk skips acquire
/// and extract entirely, which is what makes re-pointing an existing install at a different
/// realm a five-second operation rather than a 16 GiB one.
/// </summary>
public class ClientInstallOrchestrator(
    SettingsService settingsService,
    GoogleDriveDownloader downloader,
    ClientArchiveExtractor extractor,
    RealmlistService realmlist,
    WowConfigService wowConfig,
    AddonCatalog catalog,
    AddonInstallService addonInstaller,
    PatchCatalog patchCatalog,
    ClientPatchService patchInstaller,
    SteamLocator steamLocator,
    CompatToolCatalog compatTools,
    SteamRuntimeCatalog steamRuntimes,
    SteamIntegrationService steamIntegration,
    LogService log
)
{
    public event Action<InstallProgress>? ProgressChanged;

    /// <summary>The Steam library entry this app creates. Also the key an install re-run matches on.</summary>
    public const string SteamAppName = "World of Warcraft 3.3.5a";

    /// <summary>Runs the install and returns the resolved client root.</summary>
    public async Task<string> RunAsync(CancellationToken ct)
    {
        var settings = settingsService.Settings;
        var installDir = AppSettings.ExpandHome(settings.InstallDir);
        var downloadDir = AppSettings.ExpandHome(settings.DownloadDir);

        var clientRoot = settings.ClientSource switch
        {
            ClientSource.ExistingFolder => UseExistingFolder(settings),
            ClientSource.LocalZip => await UnpackAsync(RequireLocalZip(settings), installDir, ct),
            _ => await UnpackAsync(await DownloadAsync(settings, downloadDir, ct), installDir, ct),
        };

        Report(InstallPhase.Configure, "Pointing the client at the server…", null);
        var written = realmlist.Apply(clientRoot, settings.ServerAddress);
        foreach (var file in written)
        {
            log.Append($"  realmlist: {file}");
        }
        ApplyDisplay(clientRoot, settings);

        // Remember where the client landed: the Addons and Steam pages both need it, and
        // re-deriving it means walking the tree again on every page load.
        settings.ClientRoot = clientRoot;
        await settingsService.SaveAsync();

        var addonOutcome = settings.InstallAddonsAfterInstall
            ? await InstallAddonsAsync(clientRoot, settings, ct)
            : null;

        var patchOutcome = settings.InstallPatchesAfterInstall
            ? await InstallPatchesAsync(clientRoot, settings, ct)
            : null;

        if (settings.SetupSteamAfterInstall)
        {
            await RunSteamSetupAsync(clientRoot, settings, ct);
        }

        var failed = (addonOutcome?.Failed ?? 0) + (patchOutcome?.Failed ?? 0);
        var summary = failed > 0
            ? $"Client ready at {clientRoot} — {failed} download(s) could not be fetched, "
                + "see the log. Everything else is installed."
            : $"Client ready at {clientRoot}";
        Report(InstallPhase.Done, summary, 1);
        return clientRoot;
    }

    /// <summary>
    /// Writes the chosen resolution into the client's own config.
    ///
    /// Never fails the install: a client at the wrong resolution is a working client, and by
    /// this point 16 GiB has been downloaded and unpacked. A resolution nobody chose leaves
    /// the file alone rather than guessing — on a machine whose displays could not be read,
    /// picking one would be inventing an answer.
    /// </summary>
    private void ApplyDisplay(string clientRoot, AppSettings settings)
    {
        if (settings.PreferredResolution is not { Length: > 0 } preferred
            || WowConfigService.ParseMode(preferred) is not { } mode)
        {
            return;
        }
        try
        {
            wowConfig.Apply(clientRoot, new DisplaySettings(mode, settings.Windowed));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Append(
                $"Could not set the resolution ({e.Message}); the client will start at its "
                    + "own default and it can be changed in the game's video options."
            );
        }
    }

    public sealed record AddonOutcome(int Installed, int Failed);

    /// <summary>
    /// Installs the selected catalog addons.
    ///
    /// A failure here never fails the install. By this point the client is downloaded,
    /// unpacked and pointed at the realm — throwing away all of that because one addon's
    /// GitHub release moved would be absurd, and addons are the one part the user can redo in
    /// seconds from the Addons page. Each failure is logged and counted instead.
    /// </summary>
    public async Task<AddonOutcome> InstallAddonsAsync(
        string clientRoot,
        AppSettings settings,
        CancellationToken ct
    )
    {
        var wanted = SelectedAddons(settings);
        if (wanted.Count == 0)
        {
            return new AddonOutcome(0, 0);
        }

        Report(InstallPhase.Addons, "Installing addons…", 0);
        var installed = 0;
        var failed = 0;
        for (var i = 0; i < wanted.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var addon = wanted[i];
            Report(
                InstallPhase.Addons,
                addon.Name,
                (double)i / wanted.Count,
                $"{i + 1} / {wanted.Count}"
            );
            try
            {
                await addonInstaller.InstallFromCatalogAsync(clientRoot, addon, null, ct);
                installed++;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                failed++;
                log.Append($"Could not install {addon.Name}: {e.Message}");
            }
        }
        log.Append(
            failed == 0
                ? $"Installed {installed} addons."
                : $"Installed {installed} addons; {failed} could not be fetched and can be "
                    + "retried from the Addons page."
        );
        return new AddonOutcome(installed, failed);
    }

    /// <summary>
    /// The catalog entries the one-click install should fetch: the user's saved choice, or the
    /// catalog's own recommended set when they have never made one.
    /// </summary>
    public List<CatalogAddon> SelectedAddons(AppSettings settings) =>
        settings.SelectedAddonIds is { } ids
            ? catalog.Entries.Where(e => ids.Contains(e.Id, StringComparer.Ordinal)).ToList()
            : catalog.Entries.Where(e => e.Recommended).ToList();

    /// <summary>
    /// Installs the selected MPQ patches. Like addons, a failure here is logged and counted
    /// rather than failing an install that has already downloaded and unpacked a client.
    /// </summary>
    public async Task<AddonOutcome> InstallPatchesAsync(
        string clientRoot,
        AppSettings settings,
        CancellationToken ct
    )
    {
        var wanted = SelectedPatches(settings);
        if (wanted.Count == 0)
        {
            return new AddonOutcome(0, 0);
        }
        Report(InstallPhase.Addons, "Installing patches…", 0);
        var installed = 0;
        var failed = 0;
        for (var i = 0; i < wanted.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var patch = wanted[i];
            Report(InstallPhase.Addons, patch.Name, (double)i / wanted.Count, $"{i + 1} / {wanted.Count}");
            try
            {
                await patchInstaller.InstallAsync(clientRoot, patch, null, ct);
                installed++;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                failed++;
                log.Append($"Could not install {patch.Name}: {e.Message}");
            }
        }
        return new AddonOutcome(installed, failed);
    }

    /// <summary>The patches the one-click install should fetch.</summary>
    public List<ClientPatch> SelectedPatches(AppSettings settings) =>
        settings.SelectedPatchIds is { } ids
            ? patchCatalog.Entries.Where(e => ids.Contains(e.Id, StringComparer.Ordinal)).ToList()
            : patchCatalog.Entries.Where(e => e.Recommended).ToList();

    /// <summary>
    /// Registers the client with Steam. Public so the Steam page can run the same pipeline on
    /// its own, against a client that was installed on an earlier run.
    /// </summary>
    public async Task RunSteamSetupAsync(
        string clientRoot,
        AppSettings settings,
        CancellationToken ct,
        Action<int, StepState, string>? report = null,
        bool preserveExistingShortcut = true
    )
    {
        Report(InstallPhase.SteamSetup, "Setting up Steam…", null);
        var steam = steamLocator.Locate()
            ?? throw new InvalidOperationException(
                "Native Steam was not found, so the shortcut can't be created. "
                    + "Flatpak and Snap Steam are not supported."
            );
        var tools = compatTools.Scan(steam.Root);
        var selection = WotlkProton.Select(
            tools,
            steamRuntimes.AvailabilityFor(steam.Root),
            settings.PreferredProtonInternalName
        );
        var tool = selection.Tool
            ?? throw new InvalidOperationException(
                "No usable Proton build was found. " + WotlkProton.Guidance
            );
        if (selection.SubstitutedFor is { } replaced)
        {
            log.Append(
                $"Using {tool.DisplayName} instead of {replaced.DisplayName}, which "
                    + WotlkProton.SubstitutionReason(replaced, steamRuntimes.AvailabilityFor(steam.Root))
                    + "."
            );
        }

        var exe = ClientLocator.ExePathIn(clientRoot)
            ?? throw new InvalidOperationException($"No {ClientLocator.ExeName} in {clientRoot}");

        await steamIntegration.RunAsync(
            new SteamSetupContext(
                steam,
                tool,
                SteamAppName,
                exe,
                SteamIntegrationService.BuildLaunchOptions([clientRoot]),
                preserveExistingShortcut
            ),
            report,
            ct
        );
    }

    private string UseExistingFolder(AppSettings settings)
    {
        var chosen = AppSettings.ExpandHome(settings.ExistingClientPath ?? "");
        var root = ClientLocator.Find(chosen)
            ?? throw new InvalidOperationException(
                $"No {ClientLocator.ExeName} found in {chosen}. Pick the folder that holds it, or its parent."
            );
        log.Append($"Using the client already at {root}; nothing will be downloaded or unpacked.");
        return root;
    }

    private static string RequireLocalZip(AppSettings settings)
    {
        var zip = AppSettings.ExpandHome(settings.LocalZipPath ?? "");
        if (!File.Exists(zip))
        {
            throw new FileNotFoundException($"The chosen zip does not exist: {zip}", zip);
        }
        return zip;
    }

    private async Task<string> DownloadAsync(AppSettings settings, string downloadDir, CancellationToken ct)
    {
        Report(InstallPhase.Acquire, "Downloading the client from Google Drive…", 0);
        var destination = Path.Join(downloadDir, GoogleDriveDownloader.ClientZipName);
        var progress = new Progress<DownloadProgress>(p =>
            Report(
                InstallPhase.Acquire,
                GoogleDriveDownloader.ClientZipName,
                p.Fraction,
                $"{GoogleDriveDownloader.Human(p.Downloaded)} / {GoogleDriveDownloader.Human(p.Total)}"
            )
        );
        return await downloader.DownloadAsync(
            settings.DriveFileId,
            destination,
            settings.ExpectedZipBytes,
            progress,
            ct
        );
    }

    private async Task<string> UnpackAsync(string zipPath, string installDir, CancellationToken ct)
    {
        Report(InstallPhase.Extract, "Unpacking the client…", 0);
        var progress = new Progress<ExtractProgress>(p =>
            Report(
                InstallPhase.Extract,
                p.CurrentEntry,
                p.Fraction,
                $"{GoogleDriveDownloader.Human(p.BytesWritten)} / {GoogleDriveDownloader.Human(p.TotalBytes)}"
            )
        );
        await extractor.ExtractAsync(zipPath, installDir, progress, ct);
        return ClientLocator.Find(installDir)
            ?? throw new InvalidOperationException(
                $"The archive unpacked but no {ClientLocator.ExeName} was found under {installDir}. "
                    + "The zip may not be a WoW client."
            );
    }

    private void Report(InstallPhase phase, string detail, double? fraction, string counter = "") =>
        ProgressChanged?.Invoke(new InstallProgress(phase, detail, fraction, counter));
}
