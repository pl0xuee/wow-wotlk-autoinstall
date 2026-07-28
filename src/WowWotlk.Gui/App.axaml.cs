using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Addons;
using WowWotlk.Gui.Services.Client;
using WowWotlk.Gui.Services.Steam;
using WowWotlk.Gui.ViewModels;
using WowWotlk.Gui.Views;

namespace WowWotlk.Gui;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider BuildServices() => Registrations().BuildServiceProvider();

    /// <summary>
    /// Every registration the app needs. Exposed so a test can validate the graph resolves
    /// without constructing anything — building the real provider writes to the user's log.
    /// </summary>
    internal static IServiceCollection Registrations() =>
        new ServiceCollection()
            .AddHttpClient()
            // GitHub's API rejects requests without a User-Agent, and this client talks to
            // api.github.com for both addon releases and the app's own updates.
            .ConfigureHttpClientDefaults(b =>
                b.ConfigureHttpClient(c =>
                    c.DefaultRequestHeaders.UserAgent.ParseAdd(
                        $"wow-wotlk-autoinstall/{AppUpdateService.CurrentVersion}"
                    )
                )
            )
            .AddSingleton<SettingsService>()
            .AddSingleton<LogService>()
            .AddSingleton<OperationRunner>()
            .AddSingleton<AppUpdateService>()
            .AddSingleton<PreflightService>()
            .AddSingleton<GoogleDriveDownloader>()
            .AddSingleton<ClientArchiveExtractor>()
            .AddSingleton<RealmlistService>()
            .AddSingleton<WowConfigService>()
            // Constructed explicitly: its sysfs root and xrandr probe are optional parameters
            // so tests can scan a fixture, and the container must not try to resolve them.
            .AddSingleton(_ => new Services.Display.DisplayCatalog())
            .AddSingleton<ClientInstallOrchestrator>()
            .AddSingleton<AddonCatalog>()
            .AddSingleton<AddonResolver>()
            .AddSingleton<InstalledAddonStore>()
            .AddSingleton<AddonInstallService>()
            .AddSingleton<AddonScanner>()
            .AddSingleton<SteamLocator>()
            // Constructed explicitly: the well-known-directories parameter is optional so
            // tests can scan a fixture, and the container must not try to resolve it.
            .AddSingleton(_ => new CompatToolCatalog())
            .AddSingleton<SteamRuntimeCatalog>()
            .AddSingleton<ShortcutsVdfService>()
            .AddSingleton<ConfigVdfService>()
            .AddSingleton<SteamProcessService>()
            .AddSingleton<ProtonPrefixService>()
            .AddSingleton<SteamGridArtService>()
            .AddSingleton<SteamIntegrationService>()
            .AddSingleton<MainViewModel>()
            .AddSingleton<InstallViewModel>()
            .AddSingleton<AddonsViewModel>()
            .AddSingleton<SteamSetupViewModel>()
            .AddSingleton<SettingsViewModel>();
}
