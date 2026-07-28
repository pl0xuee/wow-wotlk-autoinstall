using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using WowWotlk.Gui.Models;
using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Addons;
using WowWotlk.Gui.Services.Client;
using WowWotlk.Gui.Services.Display;
using WowWotlk.Gui.Services.Patches;

namespace WowWotlk.Gui.ViewModels;

/// <summary>
/// One tickable catalog addon on the Install page. Mutable and observable rather than a
/// record, because the tick box writes back to it directly.
/// </summary>
public partial class AddonChoice(string id, string name, string category, bool selected)
    : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Category { get; } = category;

    [ObservableProperty]
    public partial bool Selected { get; set; } = selected;
}

/// <summary>
/// One resolution the picker offers, and where it came from. "Native" is the primary display's
/// own mode — the one a user almost always wants, and the reason the list is not just numbers.
/// </summary>
public sealed record ResolutionRow(string Value, string Detail)
{
    public override string ToString() => Detail.Length == 0 ? Value : $"{Value}   {Detail}";
}

/// <summary>One preflight result. The booleans drive Avalonia's conditional style classes.</summary>
public sealed record CheckRow(string Name, string Detail, CheckState State)
{
    public string Glyph => State switch
    {
        CheckState.Ok => "✦",
        CheckState.Warn => "!",
        _ => "✕",
    };

    public bool IsOk => State == CheckState.Ok;
    public bool IsWarn => State == CheckState.Warn;
    public bool IsFail => State == CheckState.Fail;
}

/// <summary>
/// One segment of the phase track across the top of the page. The track is the page's centre
/// of gravity during a run: it answers "which of the four long things is happening, and how
/// far in" without the user reading a log.
/// </summary>
public sealed record PhaseSegment(string Name, InstallPhase Phase)
{
    public bool IsActive { get; init; }
    public bool IsDone { get; init; }
}

public partial class InstallViewModel : ViewModelBase
{
    public ObservableCollection<CheckRow> Checks { get; } = [];
    public ObservableCollection<PhaseSegment> Phases { get; } = [];

    [ObservableProperty]
    public partial string InstallDir { get; set; }

    [ObservableProperty]
    public partial string DownloadDir { get; set; }

    [ObservableProperty]
    public partial string ServerAddress { get; set; }

    [ObservableProperty]
    public partial bool SetupSteamAfterInstall { get; set; }

    [ObservableProperty]
    public partial bool SourceIsDrive { get; set; }

    [ObservableProperty]
    public partial bool SourceIsZip { get; set; }

    [ObservableProperty]
    public partial bool SourceIsFolder { get; set; }

    [ObservableProperty]
    public partial string? LocalZipPath { get; set; }

    [ObservableProperty]
    public partial string? ExistingClientPath { get; set; }

    /// <summary>Byte counter shown large during acquire/extract; empty when idle.</summary>
    [ObservableProperty]
    public partial string PhaseCounter { get; set; } = "";

    // An idle page should say what will happen next rather than sit blank.
    [ObservableProperty]
    public partial string PhaseDetail { get; set; } =
        "Pick where the client comes from and where it goes, set the realm, then install.";

    [ObservableProperty]
    public partial double PhaseFraction { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsCheckingPreflight { get; set; }

    /// <summary>Set once an install finishes, so the page can say where the client landed.</summary>
    [ObservableProperty]
    public partial string? InstalledClientRoot { get; set; }

    /// <summary>The configured Drive file id, shown so it is obvious which upload will be fetched.</summary>
    [ObservableProperty]
    public partial string DriveFileId { get; set; } = "";

    public bool HasDriveFileId => DriveFileId.Length > 0;

    partial void OnDriveFileIdChanged(string value) => OnPropertyChanged(nameof(HasDriveFileId));

    public ObservableCollection<ResolutionRow> Resolutions { get; } = [];

    [ObservableProperty]
    public partial ResolutionRow? SelectedResolution { get; set; }

    [ObservableProperty]
    public partial bool Windowed { get; set; }

    [ObservableProperty]
    public partial string ResolutionNote { get; set; } = "";

    partial void OnSelectedResolutionChanged(ResolutionRow? value)
    {
        if (_suppressWriteBack)
        {
            return;
        }
        _settingsService.Settings.PreferredResolution = value?.Value;
    }

    partial void OnWindowedChanged(bool value)
    {
        if (!_suppressWriteBack)
        {
            _settingsService.Settings.Windowed = value;
        }
    }

    [ObservableProperty]
    public partial bool InstallPatches { get; set; }

    /// <summary>Game-data patches, kept apart from addons because they are a different thing.</summary>
    public ObservableCollection<AddonChoice> PatchChoices { get; } = [];

    public string PatchSummary =>
        !InstallPatches ? "No patches will be installed."
        : PatchChoices.Count(c => c.Selected) is var n && n == 0
            ? "No patches ticked."
            : $"{n} patch{(n == 1 ? "" : "es")} will be installed into the client's Data folder.";

    partial void OnInstallPatchesChanged(bool value)
    {
        if (!_suppressWriteBack)
        {
            _settingsService.Settings.InstallPatchesAfterInstall = value;
        }
        OnPropertyChanged(nameof(PatchSummary));
    }

    private void SavePatchSelection()
    {
        _settingsService.Settings.SelectedPatchIds =
            [.. PatchChoices.Where(c => c.Selected).Select(c => c.Id)];
        OnPropertyChanged(nameof(PatchSummary));
    }

    [ObservableProperty]
    public partial bool InstallAddons { get; set; }

    /// <summary>Every catalog entry with a tick box, so the one-click set is visible and editable.</summary>
    public ObservableCollection<AddonChoice> AddonChoices { get; } = [];

    public string AddonSummary =>
        !InstallAddons ? "No addons will be installed."
        : AddonChoices.Count(c => c.Selected) is var n && n == 0
            ? "No addons ticked."
            : $"{n} addon{(n == 1 ? "" : "s")} will be installed after the client.";

    partial void OnInstallAddonsChanged(bool value)
    {
        if (!_suppressWriteBack)
        {
            _settingsService.Settings.InstallAddonsAfterInstall = value;
        }
        OnPropertyChanged(nameof(AddonSummary));
    }

    // Set while OnShown copies the shared settings into this page's fields, so those writes
    // are not mistaken for the user editing them and pushed straight back out again.
    private bool _suppressWriteBack;

    /// <summary>
    /// Persists the ticked set. Written as an explicit list even when it matches the
    /// recommended set, so a later change to the shipped catalog cannot silently add an addon
    /// to somebody's install behind their back.
    /// </summary>
    private void SaveAddonSelection()
    {
        _settingsService.Settings.SelectedAddonIds =
            [.. AddonChoices.Where(c => c.Selected).Select(c => c.Id)];
        OnPropertyChanged(nameof(AddonSummary));
    }

    /// <summary>
    /// Also consults the shared runner, not just this page. Only one operation runs at a time
    /// across the whole app, so a button enabled while an addon install or Steam setup is in
    /// flight is a button that wipes this page's progress track and then gets refused.
    /// </summary>
    public bool CanInstall =>
        !IsRunning && !_runner.IsBusy && !Checks.Any(c => c.State == CheckState.Fail);

    public InstallViewModel(
        SettingsService settingsService,
        OperationRunner runner,
        ClientInstallOrchestrator orchestrator,
        PreflightService preflight,
        AddonCatalog catalog,
        PatchCatalog patches,
        DisplayCatalog displays,
        LogService log
    )
    {
        _settingsService = settingsService;
        _runner = runner;
        _preflight = preflight;
        _displays = displays;
        _log = log;

        var settings = settingsService.Settings;
        InstallAddons = settings.InstallAddonsAfterInstall;
        InstallPatches = settings.InstallPatchesAfterInstall;
        var chosenPatches = orchestrator.SelectedPatches(settings).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in patches.Entries)
        {
            var choice = new AddonChoice(entry.Id, entry.Name, "", chosenPatches.Contains(entry.Id));
            choice.PropertyChanged += (_, _) => SavePatchSelection();
            PatchChoices.Add(choice);
        }

        var chosen = orchestrator.SelectedAddons(settings).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in catalog.Entries)
        {
            var choice = new AddonChoice(entry.Id, entry.Name, entry.Category, chosen.Contains(entry.Id));
            choice.PropertyChanged += (_, _) => SaveAddonSelection();
            AddonChoices.Add(choice);
        }
        InstallDir = settings.InstallDir;
        DownloadDir = settings.DownloadDir;
        ServerAddress = settings.ServerAddress;
        SetupSteamAfterInstall = settings.SetupSteamAfterInstall;
        LocalZipPath = settings.LocalZipPath;
        ExistingClientPath = settings.ExistingClientPath;
        InstalledClientRoot = settings.ClientRoot;
        DriveFileId = settings.DriveFileId;
        SourceIsDrive = settings.ClientSource == ClientSource.GoogleDrive;
        SourceIsZip = settings.ClientSource == ClientSource.LocalZip;
        SourceIsFolder = settings.ClientSource == ClientSource.ExistingFolder;
        ResetPhases();
        LoadResolutions();

        // Any operation anywhere in the app changes whether this page's button should be live.
        runner.Started += _ => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(CanInstall)));
        runner.Completed += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(CanInstall)));

        // Sampled, for the same reason MainViewModel samples it: extraction raises one event
        // per entry and a client archive holds tens of thousands, so posting each to the UI
        // thread backs it up for seconds at a time — the window stops repainting, Cancel stops
        // responding, and the page runs thousands of entries behind what is on disk.
        Observable
            .FromEvent<InstallProgress>(
                h => orchestrator.ProgressChanged += h,
                h => orchestrator.ProgressChanged -= h
            )
            .Sample(TimeSpan.FromMilliseconds(150))
            .Subscribe(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    PhaseDetail = p.Detail;
                    PhaseFraction = p.Fraction ?? 0;
                    PhaseCounter = p.Counter;
                    SetActivePhase(p.Phase);
                })
            );

        _ = RefreshChecksAsync();
    }

    partial void OnSourceIsDriveChanged(bool value) => OnSourceChanged(value, ClientSource.GoogleDrive);

    partial void OnSourceIsZipChanged(bool value) => OnSourceChanged(value, ClientSource.LocalZip);

    partial void OnSourceIsFolderChanged(bool value) => OnSourceChanged(value, ClientSource.ExistingFolder);

    private void OnSourceChanged(bool selected, ClientSource source)
    {
        // Radio buttons raise both the deselect and the select; only the select carries news.
        if (!selected)
        {
            return;
        }
        _settingsService.Settings.ClientSource = source;
        _ = RefreshChecksAsync();
    }

    [RelayCommand]
    private async Task PickInstallDirAsync()
    {
        if (await PathPicker.PickFolderAsync("Where should the client be installed?", InstallDir) is { } dir)
        {
            InstallDir = dir;
            await SaveAndRecheckAsync();
        }
    }

    [RelayCommand]
    private async Task PickDownloadDirAsync()
    {
        if (await PathPicker.PickFolderAsync("Where should the zip be downloaded?", DownloadDir) is { } dir)
        {
            DownloadDir = dir;
            await SaveAndRecheckAsync();
        }
    }

    [RelayCommand]
    private async Task PickZipAsync()
    {
        if (await PathPicker.PickZipAsync("Select the client zip", LocalZipPath ?? DownloadDir) is { } file)
        {
            LocalZipPath = file;
            SourceIsZip = true;
            await SaveAndRecheckAsync();
        }
    }

    [RelayCommand]
    private async Task PickClientFolderAsync()
    {
        if (await PathPicker.PickFolderAsync("Select the existing client folder", ExistingClientPath ?? InstallDir) is { } dir)
        {
            ExistingClientPath = dir;
            SourceIsFolder = true;
            await SaveAndRecheckAsync();
        }
    }

    /// <summary>
    /// Re-runs preflight on the way in. Free disk space and the set of installed Proton builds
    /// both change while the app is open, and a stale green check is worse than no check.
    /// </summary>
    public override void OnShown()
    {
        // Re-read everything this page also writes, not just the fields it only displays.
        //
        // The Settings page edits the same AppSettings object and writes through on every
        // keystroke, while this page keeps its own copy and pushes all of it back on install.
        // Refreshing only some fields is worse than refreshing none: the stale ones are then
        // written over the user's new values, so a client goes to the folder they replaced and
        // connects to the realm they replaced, and settings.json ends up holding the old ones.
        var settings = _settingsService.Settings;
        _suppressWriteBack = true;
        try
        {
            InstallDir = settings.InstallDir;
            DownloadDir = settings.DownloadDir;
            ServerAddress = settings.ServerAddress;
            SetupSteamAfterInstall = settings.SetupSteamAfterInstall;
            LocalZipPath = settings.LocalZipPath;
            ExistingClientPath = settings.ExistingClientPath;
            DriveFileId = settings.DriveFileId;
            InstallAddons = settings.InstallAddonsAfterInstall;
            InstallPatches = settings.InstallPatchesAfterInstall;
            InstalledClientRoot = settings.ClientRoot;
            Windowed = settings.Windowed;
            SelectedResolution =
                Resolutions.FirstOrDefault(r => r.Value == settings.PreferredResolution)
                ?? SelectedResolution;
        }
        finally
        {
            _suppressWriteBack = false;
        }
        _ = RunChecksAsync();
    }

    [RelayCommand]
    private Task RefreshChecksAsync() => RunChecksAsync();

    [RelayCommand]
    private async Task InstallAsync()
    {
        await SaveAsync();
        IsRunning = true;
        ResetPhases();
        PhaseDetail = "Starting…";
        var result = await _runner.RunAsync(
            "Install client",
            async (services, ct) =>
            {
                var orchestrator = services.GetRequiredService<ClientInstallOrchestrator>();
                var root = await orchestrator.RunAsync(ct);
                // Marshalled: this delegate runs on a thread-pool thread, and the property is
                // bound to a visibility flip that invalidates layout.
                Dispatcher.UIThread.Post(() => InstalledClientRoot = root);
            }
        );
        IsRunning = false;
        PhaseCounter = "";
        switch (result.Outcome)
        {
            case OperationOutcome.Succeeded:
                PhaseDetail = $"Client ready at {InstalledClientRoot}";
                PhaseFraction = 1;
                SetActivePhase(InstallPhase.Done);
                break;
            case OperationOutcome.Cancelled:
                PhaseDetail = "Cancelled. Nothing was left half-applied — run it again to resume.";
                break;
            default:
                // Without this the page keeps the failing segment lit and shows whatever the
                // last progress line happened to be, so a failed install reads as one still
                // running. The reason belongs where the user was looking.
                PhaseDetail = result.Error?.Message ?? "The install failed. See the log for details.";
                break;
        }
        await RunChecksAsync();
    }

    private async Task SaveAndRecheckAsync()
    {
        await SaveAsync();
        await RunChecksAsync();
    }

    private async Task SaveAsync()
    {
        var settings = _settingsService.Settings;
        settings.InstallDir = InstallDir;
        settings.DownloadDir = DownloadDir;
        settings.ServerAddress = ServerAddress;
        settings.SetupSteamAfterInstall = SetupSteamAfterInstall;
        settings.LocalZipPath = LocalZipPath;
        settings.ExistingClientPath = ExistingClientPath;
        await _settingsService.SaveAsync();
    }

    private async Task RunChecksAsync()
    {
        // Preflight touches the filesystem and scans compatibilitytools.d; keep it off the UI
        // thread so switching source or picking a folder stays instant.
        IsCheckingPreflight = true;
        try
        {
            var source = _settingsService.Settings.ClientSource;
            var installDir = AppSettings.ExpandHome(InstallDir);
            var downloadDir = AppSettings.ExpandHome(DownloadDir);
            var results = await Task.Run(() => _preflight.RunAsync(installDir, downloadDir, source));
            Checks.Clear();
            foreach (var check in results)
            {
                Checks.Add(new CheckRow(check.Name, check.Detail, check.State));
            }
        }
        catch (Exception e)
        {
            _log.Append($"Preflight failed: {e.Message}");
        }
        finally
        {
            IsCheckingPreflight = false;
            OnPropertyChanged(nameof(CanInstall));
        }
    }

    /// <summary>
    /// Fills the picker from the modes this machine's displays actually report, and selects the
    /// saved choice — or the primary display's native mode when there is none.
    ///
    /// Offering only real modes matters: a 3.3.5a client asked for a resolution its monitor
    /// cannot show starts to a black screen, and the way out is editing Config.wtf by hand,
    /// which is exactly what this picker exists to avoid.
    /// </summary>
    private void LoadResolutions()
    {
        var choices = _displays.Choices();
        var saved = _settingsService.Settings.PreferredResolution;
        _suppressWriteBack = true;
        try
        {
            Resolutions.Clear();
            foreach (var choice in choices)
            {
                var where = choice.Displays.Count > 1
                    ? $"{choice.Displays.Count} displays"
                    : string.Join(", ", choice.Displays);
                Resolutions.Add(
                    new ResolutionRow(
                        choice.Mode.ToString(),
                        choice.IsPrimaryNative ? $"native · {where}" : where
                    )
                );
            }

            SelectedResolution =
                Resolutions.FirstOrDefault(r => r.Value == saved) ?? Resolutions.FirstOrDefault();
            Windowed = _settingsService.Settings.Windowed;
        }
        finally
        {
            _suppressWriteBack = false;
        }

        // The selection above was suppressed to avoid a write-back, so persist the resolved
        // default explicitly — otherwise the install would write nothing on a first run.
        _settingsService.Settings.PreferredResolution = SelectedResolution?.Value;

        ResolutionNote = Resolutions.Count switch
        {
            0 => "No displays could be read, so the client will be left at its own default. "
                + "Set the resolution in the game's video options.",
            _ when saved is { Length: > 0 } && Resolutions.All(r => r.Value != saved) =>
                $"The saved resolution {saved} is not offered by any connected display; "
                    + $"{SelectedResolution?.Value} will be used instead.",
            _ => "Written to WTF/Config.wtf, so the first launch is already the right size. "
                + "Only modes your displays report are listed. A client nobody has played yet "
                + "also gets the game's own Ultra preset, 4x anti-aliasing and a software "
                + "cursor; one you have played keeps its graphics settings.",
        };
    }

    private void ResetPhases()
    {
        Phases.Clear();
        foreach (var (name, phase) in TrackPhases)
        {
            Phases.Add(new PhaseSegment(name, phase));
        }
        PhaseFraction = 0;
        PhaseCounter = "";
    }

    /// <summary>
    /// Marks everything before the running phase done. The track is a progress narrative, so a
    /// phase that was skipped (no download when the client is already on disk) still reads as
    /// completed rather than leaving a hole behind the cursor.
    /// </summary>
    private void SetActivePhase(InstallPhase phase)
    {
        // Replacing every segment on every progress event re-runs the whole ItemsControl's
        // item generation — measurably the larger half of the cost when the events arrive at
        // extraction speed. The track only changes when the phase does.
        if (phase == _shownPhase)
        {
            return;
        }
        _shownPhase = phase;

        var index = Array.FindIndex(TrackPhases, p => p.Phase == phase);
        if (index < 0)
        {
            // Done: everything behind it is finished.
            index = phase == InstallPhase.Done ? TrackPhases.Length : -1;
        }
        for (var i = 0; i < Phases.Count; i++)
        {
            Phases[i] = Phases[i] with { IsActive = i == index, IsDone = index > i };
        }
    }

    private InstallPhase? _shownPhase;

    private static readonly (string Name, InstallPhase Phase)[] TrackPhases =
    [
        ("ACQUIRE", InstallPhase.Acquire),
        ("EXTRACT", InstallPhase.Extract),
        ("REALM", InstallPhase.Configure),
        ("ADDONS", InstallPhase.Addons),
        ("STEAM", InstallPhase.SteamSetup),
    ];

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanInstall));

    private readonly SettingsService _settingsService;
    private readonly OperationRunner _runner;
    private readonly PreflightService _preflight;
    private readonly DisplayCatalog _displays;
    private readonly LogService _log;
}
