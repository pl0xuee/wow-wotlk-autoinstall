using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using WowWotlk.Gui.Models;
using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Addons;
using WowWotlk.Gui.Services.Client;

namespace WowWotlk.Gui.ViewModels;

/// <summary>
/// A catalog entry as the grid shows it: the curated metadata plus what this machine knows —
/// whether it is installed and at which version.
/// </summary>
public sealed record CatalogRow(CatalogAddon Addon, InstalledAddon? Installed)
{
    public string Id => Addon.Id;
    public string Name => Addon.Name;
    public string Category => Addon.Category;
    public string Description => Addon.Description;
    public string? Homepage => Addon.Homepage;

    public bool IsInstalled => Installed is not null;

    /// <summary>The version on disk, or a dash. Shown in monospace — it is a machine value.</summary>
    public string VersionText => Installed?.Version ?? "—";

    public string ActionText => IsInstalled ? "Update" : "Install";

    /// <summary>
    /// Only a GitHub-backed entry has a version to compare, so only it can be meaningfully
    /// re-checked. A fixed-URL entry reinstalls whatever the link currently serves, which is
    /// still useful but is not an update in any sense the app can verify.
    /// </summary>
    public bool CanCheckForUpdates => Addon.Source.Kind == AddonSourceKind.Github;
}

/// <summary>A folder on disk under Interface/AddOns, with the .toc facts the row displays.</summary>
public sealed record InstalledRow(AddonFolder Folder, InstalledAddon? Record)
{
    public string Name => Folder.Name;
    public string Title => Folder.Title ?? Folder.Name;
    public string VersionText => Folder.Version ?? Record?.Version ?? "—";
    public string InterfaceText => Folder.Interface ?? "—";
    public bool Enabled => Folder.Enabled;

    /// <summary>Drives the legendary-orange badge: the number the .toc declares is not 3.3.5a's.</summary>
    public bool InterfaceMismatch => Folder.InterfaceMismatch;

    public string InterfaceHint =>
        InterfaceMismatch
            ? $"Built for interface {Folder.Interface}, not 3.3.5a's {AddonFolder.WotlkInterface}. "
                + "Tick \"Load out of date AddOns\" on the character screen if it doesn't appear."
            : "";

    /// <summary>Only an addon this app installed can be removed cleanly — it knows every folder that addon owns.</summary>
    public bool CanRemove => Record is not null;
}

public partial class AddonsViewModel : ViewModelBase
{
    public ObservableCollection<CatalogRow> Catalog { get; } = [];
    public ObservableCollection<InstalledRow> Installed { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial string? ManualUrl { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "";

    /// <summary>Null when no client has been installed yet — every command on this page needs one.</summary>
    [ObservableProperty]
    public partial string? ClientRoot { get; set; }

    public bool HasClient => ClientRoot is not null;

    public bool CanAct => HasClient && !IsBusy;

    public AddonsViewModel(
        SettingsService settingsService,
        AddonCatalog catalog,
        InstalledAddonStore store,
        AddonScanner scanner,
        OperationRunner runner,
        LogService log
    )
    {
        _settingsService = settingsService;
        _catalog = catalog;
        _store = store;
        _scanner = scanner;
        _runner = runner;
        _log = log;
        Refresh();
    }

    /// <summary>
    /// Re-scans on the way in, so a client installed since startup — or an addon dropped into
    /// Interface/AddOns by hand — shows up without the user hunting for Rescan.
    /// </summary>
    public override void OnShown() => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        var settings = _settingsService.Settings;
        // The install records the resolved root; fall back to searching the install folder so
        // a client installed before this app existed, or by hand, still shows up here.
        ClientRoot = settings.ClientRoot is { } saved && Directory.Exists(saved)
            ? saved
            : ClientLocator.Find(AppSettings.ExpandHome(settings.InstallDir));

        _store.Load();
        RebuildCatalog();
        RebuildInstalled();
        StatusMessage = HasClient
            ? ""
            : "No client found yet. Install one on the Install page, or point the installer at a folder you already have.";
    }

    partial void OnFilterChanged(string value) => RebuildCatalog();

    partial void OnClientRootChanged(string? value)
    {
        OnPropertyChanged(nameof(HasClient));
        OnPropertyChanged(nameof(CanAct));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAct));

    [RelayCommand]
    private Task InstallFromCatalogAsync(CatalogRow? row) =>
        row is null || ClientRoot is not { } root
            ? Task.CompletedTask
            : RunAsync(
                $"Install {row.Name}",
                (services, ct) =>
                    services
                        .GetRequiredService<AddonInstallService>()
                        .InstallFromCatalogAsync(root, row.Addon, null, ct)
            );

    [RelayCommand]
    private Task InstallFromUrlAsync()
    {
        if (ClientRoot is not { } root || string.IsNullOrWhiteSpace(ManualUrl))
        {
            return Task.CompletedTask;
        }
        var url = ManualUrl.Trim();
        return RunAsync(
            "Install addon from URL",
            (services, ct) =>
                services.GetRequiredService<AddonInstallService>().InstallFromUrlAsync(root, url, null, ct)
        );
    }

    [RelayCommand]
    private async Task InstallFromZipAsync()
    {
        if (ClientRoot is not { } root)
        {
            return;
        }
        if (await PathPicker.PickZipAsync("Select an addon zip", null) is not { } zip)
        {
            return;
        }
        await RunAsync(
            $"Install {Path.GetFileName(zip)}",
            (services, ct) =>
                services.GetRequiredService<AddonInstallService>().InstallFromZipAsync(root, zip, ct)
        );
    }

    [RelayCommand]
    private Task RemoveAsync(InstalledRow? row) =>
        row?.Record is null || ClientRoot is not { } root
            ? Task.CompletedTask
            : RunAsync(
                $"Remove {row.Title}",
                (services, ct) =>
                    services.GetRequiredService<AddonInstallService>().RemoveAsync(root, row.Record)
            );

    [RelayCommand]
    private Task ToggleAsync(InstalledRow? row) =>
        row is null || ClientRoot is not { } root
            ? Task.CompletedTask
            : RunAsync(
                row.Enabled ? $"Disable {row.Title}" : $"Enable {row.Title}",
                (services, _) =>
                {
                    services
                        .GetRequiredService<AddonInstallService>()
                        .SetEnabled(root, row.Name, !row.Enabled);
                    return Task.CompletedTask;
                }
            );

    [RelayCommand]
    private void OpenHomepage(CatalogRow? row)
    {
        if (row?.Homepage is { } url && !SafeUrl.TryOpenInBrowser(url))
        {
            _log.Append($"Could not open {url}");
        }
    }

    /// <summary>
    /// Every command on this page mutates the same Interface/AddOns tree, so they all go
    /// through the one runner that serialises operations — and every one of them ends by
    /// re-reading disk, because disk is the truth about what is installed.
    /// </summary>
    private async Task RunAsync(string name, Func<IServiceProvider, CancellationToken, Task> work)
    {
        IsBusy = true;
        var result = await _runner.RunAsync(name, work);
        IsBusy = false;
        StatusMessage = result.Outcome switch
        {
            OperationOutcome.Succeeded => $"{name}: done",
            OperationOutcome.Cancelled => $"{name}: cancelled",
            _ => $"{name} failed — {result.Error?.Message}",
        };
        Dispatcher.UIThread.Post(Refresh);
    }

    private void RebuildCatalog()
    {
        var needle = Filter.Trim();
        Catalog.Clear();
        foreach (var entry in _catalog.Entries)
        {
            if (needle.Length > 0
                && !entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !entry.Category.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !entry.Description.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Catalog.Add(new CatalogRow(entry, _store.ById(entry.Id)));
        }
    }

    private void RebuildInstalled()
    {
        Installed.Clear();
        if (ClientRoot is not { } root)
        {
            return;
        }
        // Match a folder back to the record that owns it. A multi-folder addon (AtlasLoot and
        // AtlasLoot_Data) has one record and several rows, so the lookup is by folder name
        // rather than by id.
        var byFolder = _store
            .All.SelectMany(record => record.Folders.Select(folder => (folder, record)))
            .ToDictionary(pair => pair.folder, pair => pair.record, StringComparer.OrdinalIgnoreCase);

        foreach (var folder in _scanner.Scan(root))
        {
            Installed.Add(new InstalledRow(folder, byFolder.GetValueOrDefault(folder.Name)));
        }
    }

    private readonly SettingsService _settingsService;
    private readonly AddonCatalog _catalog;
    private readonly InstalledAddonStore _store;
    private readonly AddonScanner _scanner;
    private readonly OperationRunner _runner;
    private readonly LogService _log;
}
