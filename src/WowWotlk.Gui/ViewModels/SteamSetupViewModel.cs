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
/// One step of the Steam pipeline, in the order <see cref="SteamIntegrationService.StepNames"/>
/// reports them. Immutable like the install page's phase segments: the collection swaps the row
/// rather than mutating it, so the row needs no change notification of its own.
/// </summary>
public sealed record StepRow(string Name)
{
    /// <summary>Null until the step is reached, so a run that has not started reads as pending.</summary>
    public StepState? State { get; init; }

    public string Detail { get; init; } = "";

    public string Glyph => State switch
    {
        StepState.Running => "◈",
        StepState.Ok => "✦",
        StepState.Failed => "✕",
        _ => "○",
    };

    public bool IsRunning => State == StepState.Running;
    public bool IsOk => State == StepState.Ok;
    public bool IsFail => State == StepState.Failed;
}

/// <summary>
/// Registers an already-installed client with Steam, on its own. The install page does this as
/// its last phase; this page exists for the client that was installed before Steam was, for the
/// shortcut a user deleted, and for the entry whose Proton mapping went missing — so it
/// rewrites the entry rather than preserving it.
/// </summary>
public partial class SteamSetupViewModel : ViewModelBase
{
    public ObservableCollection<StepRow> Steps { get; } =
        [.. SteamIntegrationService.StepNames.Select(name => new StepRow(name))];

    /// <summary>Same checklist type, glyphs and colours as the install page's preflight.</summary>
    public ObservableCollection<CheckRow> Checks { get; } = [];

    public ObservableCollection<CompatTool> ProtonBuilds { get; } = [];

    /// <summary>
    /// The Steam library entry this page writes. Fixed, and shown read-only: it is also the key
    /// an install re-run matches on, so a renamed entry would be duplicated rather than reused.
    /// </summary>
    public string ShortcutName => ClientInstallOrchestrator.SteamAppName;

    [ObservableProperty]
    public partial CompatTool? SelectedProton { get; set; }

    [ObservableProperty]
    public partial string ProtonDetail { get; set; } = "";

    [ObservableProperty]
    public partial string? ClientRoot { get; set; }

    [ObservableProperty]
    public partial bool HasClient { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    /// <summary>
    /// Every precondition the button depends on, so an unmet one greys it out instead of being
    /// explained only after the click.
    /// </summary>
    public bool CanSetUpSteam =>
        !IsBusy && !IsChecking && HasClient && !Checks.Any(c => c.State == CheckState.Fail);

    public SteamSetupViewModel(
        SettingsService settingsService,
        OperationRunner runner,
        SteamLocator steamLocator,
        CompatToolCatalog compatTools,
        SteamRuntimeCatalog steamRuntimes,
        LogService log
    )
    {
        _settingsService = settingsService;
        _runner = runner;
        _steamLocator = steamLocator;
        _compatTools = compatTools;
        _steamRuntimes = steamRuntimes;
        _log = log;

        // One operation owns the whole app, so this page must not offer a second one: a click
        // during an install would come back as "another operation is running" and read as if
        // the install itself had failed.
        runner.Started += _ => Dispatcher.UIThread.Post(() => IsBusy = true);
        runner.Completed += (_, _) => Dispatcher.UIThread.Post(() => IsBusy = false);

        _ = RefreshAsync();
    }

    /// <summary>
    /// Pins the build so the install page, preflight and this page all agree on it. Only a
    /// deliberate choice counts — a rescan re-selects programmatically, and persisting that
    /// would silently turn an auto-pick into a pin the next scan has to honour.
    /// </summary>
    partial void OnSelectedProtonChanged(CompatTool? value)
    {
        ProtonDetail = value is null
            ? WotlkProton.Guidance
            : $"{value.InternalName} — {WotlkProton.Describe(WotlkProton.Evaluate(value))}";
        if (_suppressProtonSave || value is null)
        {
            return;
        }
        _ = PinProtonAsync(value.InternalName);
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSetUpSteam));

    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(CanSetUpSteam));

    partial void OnHasClientChanged(bool value) => OnPropertyChanged(nameof(CanSetUpSteam));

    /// <summary>
    /// Re-checks on the way in. A client installed since this page was constructed, or a
    /// Proton build installed while the app was open, would otherwise not show up here until
    /// the user found the Re-check button.
    /// </summary>
    public override void OnShown() => _ = RefreshAsync();

    /// <summary>Re-runs the requirement checks and rescans compatibilitytools.d.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsChecking = true;
        try
        {
            var settings = _settingsService.Settings;
            var pinned = settings.PreferredProtonInternalName;
            // The install remembers where the client landed; before the first install there is
            // nothing to remember, so fall back to the folder it would be installed into.
            var searchDir = AppSettings.ExpandHome(settings.ClientRoot ?? settings.InstallDir);

            // Locating Steam parses loginusers.vdf, scanning Proton reads every
            // compatibilitytool.vdf and finding the client walks the install tree. None of that
            // belongs on the UI thread.
            var scan = await Task.Run(() =>
            {
                var steam = _steamLocator.Locate();
                var tools = _compatTools.Scan(steam?.Root);
                var runtimeInstalled = _steamRuntimes.AvailabilityFor(steam?.Root);
                return (
                    Steam: steam,
                    Tools: tools,
                    Client: ClientLocator.Find(searchDir),
                    Proton: PreflightService.ProtonCheck(tools, runtimeInstalled, pinned),
                    Pick: WotlkProton.Select(tools, runtimeInstalled, pinned).Tool
                );
            });

            ClientRoot = scan.Client;
            HasClient = scan.Client is not null;

            Checks.Clear();
            Checks.Add(
                scan.Steam is null
                    ? new CheckRow(
                        "Steam",
                        "Native Steam was not found. Flatpak and Snap Steam are not supported.",
                        CheckState.Fail
                    )
                    : new CheckRow("Steam", $"{scan.Steam.Root} · user {scan.Steam.UserId}", CheckState.Ok)
            );
            Checks.Add(
                scan.Client is null
                    ? new CheckRow(
                        "Client",
                        $"No {ClientLocator.ExeName} under {searchDir}",
                        CheckState.Fail
                    )
                    : new CheckRow("Client", scan.Client, CheckState.Ok)
            );
            Checks.Add(new CheckRow(scan.Proton.Name, scan.Proton.Detail, scan.Proton.State));

            _suppressProtonSave = true;
            try
            {
                ProtonBuilds.Clear();
                foreach (var tool in scan.Tools)
                {
                    ProtonBuilds.Add(tool);
                }
                SelectedProton = scan.Pick;
            }
            finally
            {
                _suppressProtonSave = false;
            }

            ResetSteps();
            StatusText = HasClient
                ? "Steam is closed and restarted while the entry is written."
                : "Install the client first, or set the client folder on the Install page.";
        }
        catch (Exception e)
        {
            Checks.Clear();
            Checks.Add(new CheckRow("Requirements", e.Message, CheckState.Fail));
            HasClient = false;
            StatusText = "Could not check the requirements. The log has the details.";
            _log.Append($"Steam page checks failed: {e.Message}");
        }
        finally
        {
            IsChecking = false;
            OnPropertyChanged(nameof(CanSetUpSteam));
        }
    }

    /// <summary>
    /// Writes the shortcut, the compatibility-tool mapping and the Proton prefix. Unlike the
    /// install path this does not preserve an existing entry — repairing one is the whole point
    /// of the page, and a shortcut left half-written is exactly what a user comes here to fix.
    /// </summary>
    [RelayCommand]
    private async Task SetUpSteamAsync()
    {
        if (IsBusy || _runner.IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            // Re-check first: Steam may have been installed, or the client moved, since the
            // page was last looked at.
            await RefreshAsync();
            if (ClientRoot is not { } clientRoot)
            {
                StatusText = "Install the client first, or set the client folder on the Install page.";
                return;
            }
            if (Checks.Any(c => c.State == CheckState.Fail))
            {
                StatusText = "Fix the failing requirements above, then run this again.";
                return;
            }

            var settings = _settingsService.Settings;
            var result = await _runner.RunAsync(
                "Steam setup",
                async (services, ct) =>
                {
                    var orchestrator = services.GetRequiredService<ClientInstallOrchestrator>();
                    await orchestrator.RunSteamSetupAsync(
                        clientRoot,
                        settings,
                        ct,
                        (index, state, detail) =>
                            Dispatcher.UIThread.Post(() => ApplyStep(index, state, detail)),
                        preserveExistingShortcut: false
                    );
                }
            );

            StatusText = result.Outcome switch
            {
                OperationOutcome.Succeeded =>
                    $"Done. '{ShortcutName}' is in your Steam library — launch it from there to play.",
                OperationOutcome.Cancelled =>
                    "Cancelled. Run it again to finish writing the entry.",
                _ => $"Steam setup failed — {result.Error?.Message}",
            };
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyStep(int index, StepState state, string detail)
    {
        if (index < 0 || index >= Steps.Count)
        {
            return;
        }
        Steps[index] = Steps[index] with
        {
            State = state,
            Detail = detail.Length > 0
                ? detail
                : state switch
                {
                    StepState.Running => "Working…",
                    StepState.Ok => "Done",
                    _ => "Failed",
                },
        };
    }

    private void ResetSteps()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i] = new StepRow(Steps[i].Name);
        }
    }

    private async Task PinProtonAsync(string internalName)
    {
        _settingsService.Settings.PreferredProtonInternalName = internalName;
        try
        {
            await _settingsService.SaveAsync();
            _log.Append($"Proton pinned to {internalName}.");
        }
        catch (Exception e)
        {
            _log.Append($"Could not save the Proton choice: {e.Message}");
        }
    }

    private bool _suppressProtonSave;
    private readonly SettingsService _settingsService;
    private readonly OperationRunner _runner;
    private readonly SteamLocator _steamLocator;
    private readonly CompatToolCatalog _compatTools;
    private readonly SteamRuntimeCatalog _steamRuntimes;
    private readonly LogService _log;
}
