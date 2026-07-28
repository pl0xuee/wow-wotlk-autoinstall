using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using WowWotlk.Gui.Models;
using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Client;

namespace WowWotlk.Gui.ViewModels;

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

    public bool CanInstall => !IsRunning && !Checks.Any(c => c.State == CheckState.Fail);

    public InstallViewModel(
        SettingsService settingsService,
        OperationRunner runner,
        ClientInstallOrchestrator orchestrator,
        PreflightService preflight,
        LogService log
    )
    {
        _settingsService = settingsService;
        _runner = runner;
        _preflight = preflight;
        _log = log;

        var settings = settingsService.Settings;
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

        orchestrator.ProgressChanged += p =>
            Dispatcher.UIThread.Post(() =>
            {
                PhaseDetail = p.Detail;
                PhaseFraction = p.Fraction ?? 0;
                PhaseCounter = p.Counter;
                SetActivePhase(p.Phase);
            });

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
        // The id is edited on the Settings page, so re-read it rather than showing the value
        // this page was constructed with.
        DriveFileId = _settingsService.Settings.DriveFileId;
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
                InstalledClientRoot = await orchestrator.RunAsync(ct);
            }
        );
        IsRunning = false;
        if (result.Outcome == OperationOutcome.Succeeded)
        {
            PhaseDetail = $"Client ready at {InstalledClientRoot}";
            PhaseCounter = "";
            PhaseFraction = 1;
            SetActivePhase(InstallPhase.Done);
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

    private static readonly (string Name, InstallPhase Phase)[] TrackPhases =
    [
        ("ACQUIRE", InstallPhase.Acquire),
        ("EXTRACT", InstallPhase.Extract),
        ("CONFIGURE", InstallPhase.Configure),
        ("STEAM", InstallPhase.SteamSetup),
    ];

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanInstall));

    private readonly SettingsService _settingsService;
    private readonly OperationRunner _runner;
    private readonly PreflightService _preflight;
    private readonly LogService _log;
}
