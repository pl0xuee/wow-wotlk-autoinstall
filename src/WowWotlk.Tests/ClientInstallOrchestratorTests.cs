using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using WowWotlk.Gui.Models;
using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Addons;
using WowWotlk.Gui.Services.Client;
using WowWotlk.Gui.Services.Steam;
using Xunit;

namespace WowWotlk.Tests;

/// <summary>
/// The "client already on disk" path end to end. It is the one route through the orchestrator
/// that touches no network and no Steam, so it can be exercised for real — and it is also the
/// route a user takes every time they re-point an existing install at a different realm.
/// </summary>
public class ClientInstallOrchestratorTests
{
    private static (ClientInstallOrchestrator Orchestrator, SettingsService Settings) Build(
        string clientDir
    )
    {
        var services = new ServiceCollection()
            .AddHttpClient()
            .AddSingleton(new LogService(null))
            .AddSingleton<SettingsService>()
            .AddSingleton<GoogleDriveDownloader>()
            .AddSingleton<ClientArchiveExtractor>()
            .AddSingleton<RealmlistService>()
            .AddSingleton<AddonCatalog>()
            .AddSingleton<AddonResolver>()
            .AddSingleton<InstalledAddonStore>()
            .AddSingleton<AddonInstallService>()
            .AddSingleton<SteamLocator>()
            .AddSingleton(_ => new CompatToolCatalog([]))
            .AddSingleton<SteamRuntimeCatalog>()
            .AddSingleton<ShortcutsVdfService>()
            .AddSingleton<ConfigVdfService>()
            .AddSingleton<SteamProcessService>()
            .AddSingleton<ProtonPrefixService>()
            .AddSingleton<SteamGridArtService>()
            .AddSingleton<SteamIntegrationService>()
            .AddSingleton<ClientInstallOrchestrator>()
            .BuildServiceProvider();

        var settingsService = services.GetRequiredService<SettingsService>();
        var settings = settingsService.Settings;
        settings.ClientSource = ClientSource.ExistingFolder;
        settings.ExistingClientPath = clientDir;
        settings.InstallDir = clientDir;
        // The two steps a test can't run: Steam setup shuts down the user's Steam, and addon
        // installs go to the network. Both are covered by their own tests.
        settings.SetupSteamAfterInstall = false;
        settings.InstallAddonsAfterInstall = false;
        return (services.GetRequiredService<ClientInstallOrchestrator>(), settingsService);
    }

    private static string MakeClient(TempDir temp)
    {
        temp.Write("client/Wow.exe", "MZ");
        temp.Write("client/Data/enUS/realmlist.wtf", "set realmlist logon.oldrepack.com\n");
        return temp.Join("client");
    }

    [Fact]
    public async Task Points_an_existing_client_at_the_configured_realm()
    {
        using var temp = new TempDir();
        var client = MakeClient(temp);
        var (orchestrator, settingsService) = Build(client);
        settingsService.Settings.ServerAddress = "192.168.1.50";

        var root = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(client, root);
        Assert.Equal(
            "set realmlist 192.168.1.50\n",
            File.ReadAllText(Path.Join(client, "Data", "enUS", "realmlist.wtf"))
        );
    }

    [Fact]
    public async Task Resolves_the_client_when_pointed_at_the_parent_folder()
    {
        using var temp = new TempDir();
        temp.Write("outer/World of Warcraft 3.3.5a/Wow.exe", "MZ");
        temp.Write("outer/World of Warcraft 3.3.5a/Data/enUS/realmlist.wtf", "");
        var (orchestrator, _) = Build(temp.Join("outer"));

        var root = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(temp.Join("outer", "World of Warcraft 3.3.5a"), root);
    }

    [Fact]
    public async Task Remembers_where_the_client_landed()
    {
        // The Addons and Steam pages both read this; deriving it again means re-walking the tree.
        using var temp = new TempDir();
        var client = MakeClient(temp);
        var (orchestrator, settingsService) = Build(client);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(client, settingsService.Settings.ClientRoot);
    }

    [Fact]
    public async Task Clears_the_cache_so_the_new_realm_is_used()
    {
        using var temp = new TempDir();
        var client = MakeClient(temp);
        temp.Write("client/Cache/WDB/enUS/creaturecache.wdb", "stale");
        var (orchestrator, _) = Build(client);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.False(Directory.Exists(Path.Join(client, "Cache")));
    }

    [Fact]
    public async Task Reports_the_phases_it_actually_ran()
    {
        // Acquire and Extract must not be reported for a client already on disk — a phase
        // track that lights up steps nothing performed is worse than no track.
        using var temp = new TempDir();
        var (orchestrator, _) = Build(MakeClient(temp));
        var phases = new List<InstallPhase>();
        orchestrator.ProgressChanged += p => phases.Add(p.Phase);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Contains(InstallPhase.Configure, phases);
        Assert.Contains(InstallPhase.Done, phases);
        Assert.DoesNotContain(InstallPhase.Acquire, phases);
        Assert.DoesNotContain(InstallPhase.Extract, phases);
    }

    [Fact]
    public async Task Says_what_is_wrong_when_the_folder_holds_no_client()
    {
        using var temp = new TempDir();
        temp.Write("empty/readme.txt");
        var (orchestrator, _) = Build(temp.Join("empty"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.RunAsync(CancellationToken.None)
        );

        Assert.Contains("Wow.exe", error.Message);
    }

    [Fact]
    public async Task Refuses_a_zip_source_pointing_at_a_missing_file()
    {
        using var temp = new TempDir();
        var (orchestrator, settingsService) = Build(MakeClient(temp));
        settingsService.Settings.ClientSource = ClientSource.LocalZip;
        settingsService.Settings.LocalZipPath = temp.Join("nope.zip");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => orchestrator.RunAsync(CancellationToken.None)
        );
    }
}
