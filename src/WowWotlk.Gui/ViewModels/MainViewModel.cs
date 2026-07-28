using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Client;

namespace WowWotlk.Gui.ViewModels;

public sealed record NavItem(string Name, string Icon, ViewModelBase Page);

public partial class MainViewModel : ViewModelBase
{
    public ObservableCollection<NavItem> NavItems { get; }
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty]
    public partial NavItem? SelectedNavItem { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial double OverallProgress { get; set; }

    // True while we know work is happening but have no numeric total to show a real fraction
    // (Configure and Steam setup report no per-item progress). The bar animates instead of
    // sitting frozen on whatever fraction the last measurable phase happened to publish.
    [ObservableProperty]
    public partial bool ProgressIsIndeterminate { get; set; }

    [ObservableProperty]
    public partial bool LogPaneOpen { get; set; }

    /// <summary>Where the full log is kept — the pane only holds the last 2000 lines.</summary>
    public string LogFilePath { get; }

    public string AppVersion { get; } = AppUpdateService.CurrentVersion;

    // Pages share one settings object and one client tree, so each re-reads on the way in.
    partial void OnSelectedNavItemChanged(NavItem? value) => value?.Page.OnShown();

    public MainViewModel(
        InstallViewModel install,
        AddonsViewModel addons,
        SteamSetupViewModel steamSetup,
        SettingsViewModel settings,
        OperationRunner runner,
        LogService log,
        ClientInstallOrchestrator orchestrator
    )
    {
        _runner = runner;
        _log = log;
        LogFilePath = log.LogFilePath ?? "(log file unavailable)";
        NavItems =
        [
            new NavItem("INSTALL", "⇣", install),
            new NavItem("ADDONS", "◈", addons),
            new NavItem("STEAM", "▶", steamSetup),
            new NavItem("SETTINGS", "⚙", settings),
        ];
        SelectedNavItem = NavItems[0];

        log.LineAdded += line =>
            Dispatcher.UIThread.Post(() =>
            {
                LogLines.Add(line);
                while (LogLines.Count > 2000)
                {
                    LogLines.RemoveAt(0);
                }
                // Mirror activity into the status bar during an operation so steps that report
                // no progress aren't silent with the log pane closed. First line only:
                // multi-line entries are error dumps that belong in the pane, not squeezed
                // into one status row.
                if (IsBusy)
                {
                    var text = StripTimestamp(line);
                    var newline = text.IndexOf('\n');
                    StatusText = (newline >= 0 ? text[..newline] : text).Trim();
                }
            });

        static string StripTimestamp(string line) =>
            line.Length > 10 && line[0] == '[' && line[9] == ']' ? line[10..] : line;

        runner.Started += name =>
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = true;
                OverallProgress = 0;
                // Start animating right away: the first phase of any operation reports no
                // fraction until it has something real to report.
                ProgressIsIndeterminate = true;
                StatusText = $"{name}…";
            });
        runner.Completed += (name, result) =>
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                StatusText = result.Outcome switch
                {
                    OperationOutcome.Succeeded => $"{name}: done",
                    OperationOutcome.Cancelled => $"{name}: cancelled",
                    _ => $"{name}: failed — {result.Error?.Message}",
                };
                if (result.Outcome == OperationOutcome.Failed)
                {
                    LogPaneOpen = true;
                }
            });

        // Extraction publishes a progress event per file and the client holds tens of
        // thousands of them; sample before touching the UI thread.
        Observable
            .FromEvent<InstallProgress>(
                h => orchestrator.ProgressChanged += h,
                h => orchestrator.ProgressChanged -= h
            )
            .Sample(TimeSpan.FromMilliseconds(150))
            .Subscribe(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = p.Detail;
                    ProgressIsIndeterminate = p.Fraction is null;
                    if (p.Fraction is { } fraction)
                    {
                        OverallProgress = fraction;
                    }
                })
            );
    }

    [RelayCommand]
    private void Cancel() => _runner.Cancel();

    [RelayCommand]
    private void ToggleLogPane() => LogPaneOpen = !LogPaneOpen;

    /// <summary>
    /// Opens the log file in the desktop's default handler. The pane is capped at 2000 lines
    /// and a client install runs for a long time, so the record needed to report a failure is
    /// routinely off the top of it.
    /// </summary>
    [RelayCommand]
    private void OpenLogFile()
    {
        if (!SafeUrl.TryOpenLocalPath(_log.LogFilePath))
        {
            _log.Append($"Could not open the log file at {LogFilePath}");
        }
    }

    private readonly OperationRunner _runner;
    private readonly LogService _log;
}
