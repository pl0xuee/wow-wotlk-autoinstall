using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using WowWotlk.Gui.Models;
using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Client;
using WowWotlk.Gui.Services.Steam;

namespace WowWotlk.Gui.ViewModels;

/// <summary>
/// Everything the app remembers, plus the two things worth doing to a client that is already
/// installed: re-pointing it at a realm, and changing the Proton build it launches under.
///
/// Every field writes straight through to the shared <see cref="AppSettings"/> as it changes,
/// so Save is only the flush to disk. The install page edits the same object; re-writing this
/// page's copy of every field on Save would silently revert whatever that page had set.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    // ── Folders ──────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string InstallDir { get; set; }

    [ObservableProperty]
    public partial string DownloadDir { get; set; }

    partial void OnInstallDirChanged(string value) => _settingsService.Settings.InstallDir = value;

    partial void OnDownloadDirChanged(string value) => _settingsService.Settings.DownloadDir = value;

    // ── Realm ────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string ServerAddress { get; set; }

    /// <summary>What the installed client's realmlist.wtf actually says, next to what it should say.</summary>
    [ObservableProperty]
    public partial CheckRow? RealmlistOnDisk { get; set; }

    [ObservableProperty]
    public partial string RealmStatus { get; set; } = "";

    [ObservableProperty]
    public partial string? ClientRoot { get; set; }

    [ObservableProperty]
    public partial bool HasClient { get; set; }

    public bool CanApplyRealmlist => HasClient && !IsBusy;

    partial void OnServerAddressChanged(string value)
    {
        _settingsService.Settings.ServerAddress = value;
        UpdateRealmComparison();
    }

    // ── Client source ────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string DriveFileId { get; set; }

    /// <summary>Held as text so a half-typed number can't be written into settings as a truncated one.</summary>
    [ObservableProperty]
    public partial string ExpectedZipBytes { get; set; }

    [ObservableProperty]
    public partial string ZipSizeNote { get; set; } = "";

    partial void OnDriveFileIdChanged(string value) =>
        _settingsService.Settings.DriveFileId = value.Trim();

    partial void OnExpectedZipBytesChanged(string value)
    {
        if (!long.TryParse(value.Trim(), out var bytes) || bytes < 0)
        {
            ZipSizeNote = "Enter the size in bytes, or 0 to skip the check. The last valid value is kept.";
            return;
        }
        _settingsService.Settings.ExpectedZipBytes = bytes;
        ZipSizeNote = bytes == 0
            ? "0 — the size check is off, and a truncated download will be unpacked."
            : GoogleDriveDownloader.Human(bytes);
    }

    // ── Proton ───────────────────────────────────────────────────────────────────────────

    public ObservableCollection<CompatTool> ProtonBuilds { get; } = [];

    [ObservableProperty]
    public partial CompatTool? SelectedProton { get; set; }

    [ObservableProperty]
    public partial string ProtonDetail { get; set; } = "";

    // Only a deliberate choice becomes a pin. A rescan re-selects programmatically, and
    // persisting that would turn an auto-pick into a pin every later scan has to honour.
    partial void OnSelectedProtonChanged(CompatTool? value)
    {
        ProtonDetail = value is null
            ? WotlkProton.Guidance
            : $"{value.InternalName} — {WotlkProton.Describe(WotlkProton.Evaluate(value))}";
        if (_suppressProtonSave || value is null)
        {
            return;
        }
        _settingsService.Settings.PreferredProtonInternalName = value.InternalName;
    }

    // ── Updates ──────────────────────────────────────────────────────────────────────────

    public string CurrentVersion => AppUpdateService.CurrentVersion;

    [ObservableProperty]
    public partial string LatestVersion { get; set; } = "not checked";

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = "";

    [ObservableProperty]
    public partial string? ReleaseUrl { get; set; }

    /// <summary>
    /// True only when there is a newer release, it ships an AppImage, and this copy is itself
    /// running from one — the app can only replace a file it was launched from.
    /// </summary>
    [ObservableProperty]
    public partial bool CanDownloadUpdate { get; set; }

    [ObservableProperty]
    public partial bool IsCheckingUpdate { get; set; }

    // ── Page state ───────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public string SettingsPath => AppSettings.SettingsPath;

    public SettingsViewModel(
        SettingsService settingsService,
        OperationRunner runner,
        RealmlistService realmlist,
        SteamLocator steamLocator,
        CompatToolCatalog compatTools,
        SteamRuntimeCatalog steamRuntimes,
        AppUpdateService appUpdate,
        LogService log
    )
    {
        _settingsService = settingsService;
        _runner = runner;
        _realmlist = realmlist;
        _steamLocator = steamLocator;
        _compatTools = compatTools;
        _steamRuntimes = steamRuntimes;
        _appUpdate = appUpdate;
        _log = log;

        var settings = settingsService.Settings;
        InstallDir = settings.InstallDir;
        DownloadDir = settings.DownloadDir;
        ServerAddress = settings.ServerAddress;
        DriveFileId = settings.DriveFileId;
        ExpectedZipBytes = settings.ExpectedZipBytes.ToString();

        // Nothing here writes to the client tree except Apply, but the Apply button and the
        // update download both share the one operation slot, so track it.
        runner.Started += _ => Dispatcher.UIThread.Post(() => IsBusy = true);
        runner.Completed += (_, _) => Dispatcher.UIThread.Post(() => IsBusy = false);

        RescanProton();
        _ = RefreshClientAsync();
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanApplyRealmlist));

    partial void OnHasClientChanged(bool value) => OnPropertyChanged(nameof(CanApplyRealmlist));

    // ── Commands ─────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task PickInstallDirAsync()
    {
        if (await PathPicker.PickFolderAsync("Where should the client be installed?", InstallDir) is { } dir)
        {
            InstallDir = dir;
            await SaveAsync();
        }
    }

    [RelayCommand]
    private async Task PickDownloadDirAsync()
    {
        if (await PathPicker.PickFolderAsync("Where should the zip be downloaded?", DownloadDir) is { } dir)
        {
            DownloadDir = dir;
            await SaveAsync();
        }
    }

    /// <summary>
    /// Writes the address into the client that is already on disk, without re-running an
    /// install. Through the runner because it rewrites files in the client tree and deletes the
    /// Cache folder — an install writing the same tree must not overlap with it.
    /// </summary>
    [RelayCommand]
    private async Task ApplyRealmlistAsync()
    {
        if (ClientRoot is not { } clientRoot)
        {
            RealmStatus = "No client installed yet. Install one on the Install page, then come back.";
            return;
        }
        await SaveAsync();
        var address = ServerAddress;
        var written = 0;
        var result = await _runner.RunAsync(
            "Apply realmlist",
            (services, ct) =>
            {
                var realmlist = services.GetRequiredService<RealmlistService>();
                written = realmlist.Apply(clientRoot, address).Count;
                return Task.CompletedTask;
            }
        );
        await RefreshClientAsync();
        RealmStatus = result.Outcome switch
        {
            OperationOutcome.Succeeded =>
                $"Wrote {written} realmlist.wtf file(s) and cleared the client's Cache folder.",
            OperationOutcome.Cancelled => "Cancelled. The client was left as it was.",
            _ => $"Could not apply the realmlist — {result.Error?.Message}",
        };
    }

    /// <summary>Rescans compatibilitytools.d and re-picks the build the install would use.</summary>
    [RelayCommand]
    private void RescanProton()
    {
        var pinned = _settingsService.Settings.PreferredProtonInternalName;
        var steam = TryLocateSteam();
        var tools = _compatTools.Scan(steam?.Root);
        _suppressProtonSave = true;
        try
        {
            ProtonBuilds.Clear();
            foreach (var tool in tools)
            {
                ProtonBuilds.Add(tool);
            }
            SelectedProton = WotlkProton
                .Select(tools, _steamRuntimes.AvailabilityFor(steam?.Root), pinned)
                .Tool;
        }
        finally
        {
            _suppressProtonSave = false;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        IsCheckingUpdate = true;
        CanDownloadUpdate = false;
        ReleaseUrl = null;
        _pendingUpdate = null;
        UpdateStatus = "Asking GitHub for the latest release…";
        try
        {
            var check = await _appUpdate.CheckAsync();
            LatestVersion = string.IsNullOrWhiteSpace(check.LatestTag) ? "unknown" : check.LatestTag;
            ReleaseUrl = string.IsNullOrWhiteSpace(check.ReleaseUrl) ? null : check.ReleaseUrl;
            if (!check.UpdateAvailable)
            {
                UpdateStatus = "This is the latest release.";
                return;
            }
            _pendingUpdate = check;
            CanDownloadUpdate =
                check.AssetUrl is not null && AppUpdateService.InstalledAppImagePath is not null;
            UpdateStatus = CanDownloadUpdate
                ? "A newer release is out. Download it here, or read the notes first."
                : check.AssetUrl is null
                    ? "A newer release is out but publishes no AppImage. Get it from the release page."
                    : "A newer release is out. This copy was not launched from an AppImage, "
                        + "so install it from the release page.";
        }
        catch (Exception e)
        {
            UpdateStatus = $"Could not check for updates — {e.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>
    /// Replaces the AppImage this copy was launched from. The running process keeps its own
    /// mounted image, so the new version only appears on the next start.
    /// </summary>
    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        var pending = _pendingUpdate;
        if (pending?.AssetUrl is not { } assetUrl)
        {
            return;
        }
        CanDownloadUpdate = false;
        var progress = new Progress<double>(p =>
            UpdateStatus = $"Downloading {pending.LatestTag}… {p:P0}"
        );
        var result = await _runner.RunAsync(
            "Download update",
            async (services, ct) =>
            {
                var updater = services.GetRequiredService<AppUpdateService>();
                await updater.DownloadAndInstallAsync(assetUrl, pending.AssetSha256, progress, ct);
            }
        );
        if (result.Outcome == OperationOutcome.Succeeded)
        {
            _log.Append($"App updated to {pending.LatestTag}; restarting into the new version.");
            UpdateStatus = $"{pending.LatestTag} is installed. Restarting…";

            // The running process keeps the old image mounted, so the new version can only run
            // as a new process. Start it first and shut down only if it actually started —
            // exiting on a failed spawn would close the app with nothing to replace it.
            if (AppUpdateService.InstalledAppImagePath is { } appImage
                && _appUpdate.TryRelaunch(appImage))
            {
                // Deferred so this handler returns and the status line above is painted; the
                // new window takes a moment to appear and a frozen one reads as a crash.
                DispatcherTimer.RunOnce(
                    () =>
                        (Application.Current?.ApplicationLifetime
                            as IClassicDesktopStyleApplicationLifetime)?.Shutdown(),
                    TimeSpan.FromMilliseconds(600)
                );
                return;
            }

            UpdateStatus =
                $"{pending.LatestTag} is installed, but this copy could not restart itself. "
                    + "Close the app and open it again to run the new version.";
            _log.Append("Could not relaunch automatically; restart the app by hand.");
            return;
        }
        CanDownloadUpdate = true;
        UpdateStatus = result.Outcome == OperationOutcome.Cancelled
            ? "Cancelled. This copy is untouched."
            : $"Update failed — {result.Error?.Message}";
    }

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        if (!SafeUrl.TryOpenInBrowser(ReleaseUrl))
        {
            UpdateStatus = "Could not open the release page in a browser.";
            _log.Append($"Refused or failed to open the release URL: {ReleaseUrl}");
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await _settingsService.SaveAsync();
            StatusText = "Saved.";
            _log.Append("Settings saved.");
        }
        catch (Exception e)
        {
            StatusText = $"Could not save — {e.Message}";
            _log.Append($"Could not save settings: {e.Message}");
        }
    }

    [RelayCommand]
    private void OpenSettingsFile()
    {
        var target = File.Exists(SettingsPath) ? SettingsPath
            : Directory.Exists(AppSettings.AppDataPath) ? AppSettings.AppDataPath
            : null;
        if (target is null)
        {
            StatusText = "Nothing written yet. Press Save to create the file.";
            return;
        }
        if (!SafeUrl.TryOpenLocalPath(target))
        {
            StatusText = "Could not hand the settings file to the desktop.";
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-reads the folders and the client on the way in. This page is constructed once at
    /// startup and the Install page edits the same settings object, so without this the boxes
    /// keep showing whatever they held when the app opened.
    /// </summary>
    public override void OnShown()
    {
        var settings = _settingsService.Settings;
        InstallDir = settings.InstallDir;
        DownloadDir = settings.DownloadDir;
        ServerAddress = settings.ServerAddress;
        _ = RefreshClientAsync();
    }

    /// <summary>
    /// Finds the installed client and reads the realmlist it currently uses, so the page can
    /// show whether the setting above and the client on disk agree.
    /// </summary>
    private async Task RefreshClientAsync()
    {
        var settings = _settingsService.Settings;
        var searchDir = AppSettings.ExpandHome(settings.ClientRoot ?? settings.InstallDir);
        try
        {
            // Finding the client walks the install tree and reading the realmlist opens files
            // under it; neither belongs on the UI thread.
            var found = await Task.Run(() =>
            {
                var root = ClientLocator.Find(searchDir);
                return (Root: root, Address: root is null ? null : _realmlist.Read(root));
            });
            ClientRoot = found.Root;
            HasClient = found.Root is not null;
            _addressOnDisk = found.Address;
        }
        catch (Exception e)
        {
            ClientRoot = null;
            HasClient = false;
            _addressOnDisk = null;
            _log.Append($"Could not read the client's realmlist: {e.Message}");
        }
        UpdateRealmComparison();
    }

    /// <summary>
    /// Compares the address in the box with the one the client actually has. Recomputed on every
    /// keystroke, so it reads from the cached on-disk value rather than the filesystem.
    /// </summary>
    private void UpdateRealmComparison()
    {
        if (!HasClient)
        {
            RealmlistOnDisk = null;
            RealmStatus = "No client installed yet. Install one on the Install page, then come back.";
            return;
        }
        if (_addressOnDisk is null)
        {
            RealmlistOnDisk = new CheckRow("On disk", "no set realmlist line", CheckState.Warn);
            RealmStatus = "The client has no realmlist line yet. Apply to write one.";
            return;
        }
        var matches = string.Equals(
            _addressOnDisk,
            RealmlistService.Normalise(ServerAddress),
            StringComparison.OrdinalIgnoreCase
        );
        RealmlistOnDisk = new CheckRow(
            "On disk",
            _addressOnDisk,
            matches ? CheckState.Ok : CheckState.Warn
        );
        RealmStatus = matches
            ? "The client already points at this address."
            : "The client points somewhere else. Apply to change it.";
    }

    /// <summary>A malformed loginusers.vdf must not take the settings page down with it.</summary>
    private SteamInstallation? TryLocateSteam()
    {
        try
        {
            return _steamLocator.Locate();
        }
        catch (Exception e)
        {
            _log.Append($"Could not read the Steam installation: {e.Message}");
            return null;
        }
    }

    private string? _addressOnDisk;
    private bool _suppressProtonSave;
    private AppUpdateCheck? _pendingUpdate;
    private readonly SettingsService _settingsService;
    private readonly OperationRunner _runner;
    private readonly RealmlistService _realmlist;
    private readonly SteamLocator _steamLocator;
    private readonly CompatToolCatalog _compatTools;
    private readonly SteamRuntimeCatalog _steamRuntimes;
    private readonly AppUpdateService _appUpdate;
    private readonly LogService _log;
}
