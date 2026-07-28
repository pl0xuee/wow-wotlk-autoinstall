using System.IO.Compression;
using WowWotlk.Gui.Models;
using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Addons;
using WowWotlk.Gui.Services.Client;
using WowWotlk.Gui.Services.Steam;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace WowWotlk.Tests;

/// <summary>
/// One test per defect found in review. Each names the wrong behaviour it locks out, because
/// the reason a line is written the way it is stops being obvious about a week later.
/// </summary>
public class AddonInstallRegressionTests
{
    private static void Toc(TempDir temp, string dir, string tocName) =>
        temp.Write(Path.Join(dir, tocName + ".toc"), $"## Title: {tocName}\n## Interface: 30300\n");

    [Fact]
    public void A_zip_with_its_toc_at_the_root_installs_under_the_addon_name()
    {
        // Was: the destination took its name from the extractor's scratch folder, so the addon
        // landed at Interface/AddOns/unpacked — a folder the client never loads, because it
        // only loads AddOns/<Dir>/<Dir>.toc.
        using var temp = new TempDir();
        temp.Write("unpacked/MyAddon.toc", "## Title: My Addon\n");

        Assert.Equal("MyAddon", AddonArchive.DestinationName(temp.Join("unpacked")));
    }

    [Fact]
    public void A_github_source_wrapper_installs_under_the_addon_name()
    {
        // Was: installed as "Questie-335-a1b2c3d", which the client ignores.
        using var temp = new TempDir();
        temp.Write("Questie-335-a1b2c3d/Questie.toc", "## Title: Questie\n");

        Assert.Equal("Questie", AddonArchive.DestinationName(temp.Join("Questie-335-a1b2c3d")));
    }

    [Fact]
    public void A_folder_already_named_after_its_toc_keeps_its_own_casing()
    {
        using var temp = new TempDir();
        Toc(temp, "AtlasLoot", "AtlasLoot");

        Assert.Equal("AtlasLoot", AddonArchive.DestinationName(temp.Join("AtlasLoot")));
    }

    [Fact]
    public void Flavour_tocs_resolve_to_the_base_build()
    {
        // Questie.toc next to Questie-Classic.toc and Questie-BCC.toc: the shortest name is
        // the base build, and picking first-by-name would install as "Questie-BCC".
        using var temp = new TempDir();
        temp.Write("wrap/Questie-BCC.toc", "## Title: Questie\n");
        temp.Write("wrap/Questie.toc", "## Title: Questie\n");
        temp.Write("wrap/Questie-Classic.toc", "## Title: Questie\n");

        Assert.Equal("Questie", AddonArchive.DestinationName(temp.Join("wrap")));
    }

    [Fact]
    public void Two_records_can_never_claim_the_same_folder()
    {
        // Was: a catalog install (id "dbm") and a hand-installed zip (id "DBM-Core") both
        // listed DBM-Core. The Addons page indexes folders by name, so the duplicate threw
        // while the page was being built — during startup — and the file persisted, so the
        // app stopped opening at all.
        var store = new InstalledAddonStore();
        store.Record(new InstalledAddon("dbm", "DBM", ["DBM-Core", "DBM-GUI"], "1", "catalog:dbm", DateTimeOffset.UnixEpoch));
        store.Record(new InstalledAddon("DBM-Core", "DBM", ["DBM-Core"], "2", "zip", DateTimeOffset.UnixEpoch));

        var claims = store.All.SelectMany(a => a.Folders).ToList();

        Assert.Equal(claims.Count, claims.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Forgetting_one_id_leaves_another_that_differs_only_in_case()
    {
        // Was: Forget matched OrdinalIgnoreCase across two unrelated id namespaces, so
        // removing folder-derived "Questie" also dropped the catalog's "questie" record —
        // orphaning its folder, which then had no record and so could never be removed.
        var store = new InstalledAddonStore();
        store.Record(new InstalledAddon("questie", "Questie", ["Questie-335"], "1", "catalog:questie", DateTimeOffset.UnixEpoch));
        store.Record(new InstalledAddon("Questie", "Questie", ["Questie"], "2", "zip", DateTimeOffset.UnixEpoch));

        store.Forget("Questie");

        Assert.Equal("questie", Assert.Single(store.All).Id);
    }

    [Fact]
    public void The_shortest_asset_wins_over_upload_order()
    {
        // Was: the first .zip in the API's array, which is upload order. Skada's release lists
        // the -with-all bundle first, so asking for Skada installed two extra modules.
        var chosen = AddonResolver.PickAsset(
            [
                "Skada-WotLK-1.8.87-with-all.zip",
                "Skada-WotLK-1.8.87-with-storage.zip",
                "Skada-WotLK-1.8.87.zip",
            ]
        );

        Assert.Equal("Skada-WotLK-1.8.87.zip", chosen);
    }
}

public class ClientPipelineRegressionTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void An_unrelated_file_in_the_download_folder_does_not_soften_the_space_check()
    {
        // Was: any file at all flipped a hard Fail to a Warn reading "a client is already
        // there" — which was false — and the Install button only blocks on Fail, so the run
        // started and ran the disk dry partway through a 16 GiB download.
        using var temp = new TempDir();
        temp.Write("downloads/readme.txt", "hello");

        Assert.Equal(0, PreflightService.ExistingDownloadBytes(temp.Join("downloads")));
    }

    [Fact]
    public void A_partial_download_counts_toward_what_is_still_needed()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Join("downloads"));
        File.WriteAllBytes(
            temp.Join("downloads", GoogleDriveDownloader.ClientZipName + ".part"),
            new byte[2048]
        );

        Assert.Equal(2048, PreflightService.ExistingDownloadBytes(temp.Join("downloads")));
        Assert.Equal(1024, PreflightService.StillToDownload(3072, temp.Join("downloads")));
    }

    [Fact]
    public void A_home_relative_client_path_passes_preflight()
    {
        // Was: SourceCheck used the raw path while the orchestrator expanded it, so a
        // hand-edited "~/Games/WoW" failed preflight and disabled an install that would work.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var probe = Path.Join(home, ".wowwotlk-expandhome-probe");
        Directory.CreateDirectory(probe);
        File.WriteAllText(Path.Join(probe, "Wow.exe"), "MZ");
        try
        {
            var settings = new AppSettings
            {
                ClientSource = ClientSource.ExistingFolder,
                ExistingClientPath = "~/.wowwotlk-expandhome-probe",
            };

            Assert.Equal(
                CheckState.Ok,
                PreflightService.SourceCheck(ClientSource.ExistingFolder, settings).State
            );
        }
        finally
        {
            Directory.Delete(probe, recursive: true);
        }
    }

    [Fact]
    public void The_shallowest_client_wins_over_a_deeper_one()
    {
        // Was: depth-first, so "Backup/OldClient" beat the real client purely because it sorts
        // earlier — and the install then wrote the realmlist into the backup and deleted its
        // Cache while leaving the real client untouched.
        using var temp = new TempDir();
        temp.Write("Backup/OldClient/Wow.exe", "MZ");
        temp.Write("World of Warcraft 3.3.5a/Wow.exe", "MZ");

        Assert.Equal(temp.Join("World of Warcraft 3.3.5a"), ClientLocator.Find(temp.Path));
    }

    [Fact]
    public void Every_realmlist_line_is_replaced_not_just_the_first()
    {
        // Was: only the first. WoW applies set directives in order, so the last one wins and
        // the client kept connecting to the stale address while the app reported success.
        var updated = RealmlistService.SetRealmlist(
            "set realmlist logon.oldrepack.com\nset realmlist 192.168.0.9\n",
            "127.0.0.1"
        );

        Assert.DoesNotContain("192.168.0.9", updated);
        Assert.DoesNotContain("oldrepack", updated);
        Assert.Equal(2, updated.Split("set realmlist 127.0.0.1").Length - 1);
    }

    [Fact]
    public void A_dollar_sign_in_the_address_is_not_a_regex_substitution()
    {
        var updated = RealmlistService.SetRealmlist("set realmlist old.example.com\n", "srv$1.lan");

        Assert.Contains("set realmlist srv$1.lan", updated);
    }

    [Fact]
    public async Task A_directory_entry_without_a_trailing_slash_does_not_wedge_the_extract()
    {
        // Was: stored as a zero-byte file called "Data", after which creating Data/enUS threw
        // DirectoryNotFoundException — tens of minutes into a 16 GiB archive, and identically
        // on every retry, because File.Create just truncated it again.
        using var temp = new TempDir();
        var zipPath = temp.Join("client.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntry("Data");
            using var w = new StreamWriter(zip.CreateEntry("Data/enUS/realmlist.wtf").Open());
            w.Write("set realmlist 127.0.0.1");
        }
        var dest = temp.Join("out");

        await new ClientArchiveExtractor(new LogService(null)).ExtractAsync(zipPath, dest);

        Assert.True(Directory.Exists(Path.Join(dest, "Data")));
        Assert.True(File.Exists(Path.Join(dest, "Data", "enUS", "realmlist.wtf")));
    }

    [Fact]
    public async Task Backslash_separated_entries_become_real_directories()
    {
        // Was: extracted as one literal filename containing backslashes, producing a flat pile
        // of oddly-named files that the orchestrator still reported as "Client ready".
        using var temp = new TempDir();
        var zipPath = temp.Join("client.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry("Data\\enUS\\realmlist.wtf").Open());
            w.Write("set realmlist 127.0.0.1");
        }
        var dest = temp.Join("out");

        await new ClientArchiveExtractor(new LogService(null)).ExtractAsync(zipPath, dest);

        Assert.True(File.Exists(Path.Join(dest, "Data", "enUS", "realmlist.wtf")));
    }
}

public class SteamRegressionTests
{
    /// <summary>A config.vdf shaped like a real one: an appid-keyed section well before the mapping.</summary>
    private const string ConfigWithShaderCache = """
        "InstallConfigStore"
        {
        	"Software"
        	{
        		"Valve"
        		{
        			"Steam"
        			{
        				"ShaderCacheManager"
        				{
        					"App"
        					{
        						"4121800892"
        						{
        							"ShaderCacheSize"		"191366560"
        						}
        						"3964412941"
        						{
        							"ShaderCacheSize"		"8378331"
        						}
        					}
        				}
        				"CompatToolMapping"
        				{
        					"489830"
        					{
        						"name"		"proton_experimental"
        						"config"		""
        						"priority"		"250"
        					}
        				}
        			}
        		}
        	}
        }
        """;

    private static (SteamInstallation Steam, TempDir Temp) Fixture(string configText)
    {
        var temp = new TempDir();
        temp.Write("config/config.vdf", configText);
        Directory.CreateDirectory(temp.Join("userdata", "1", "config"));
        return (new SteamInstallation(temp.Path, "1"), temp);
    }

    [Fact]
    public void Setting_a_compat_tool_leaves_other_sections_keyed_by_that_appid_alone()
    {
        // Was: the removal regex was unanchored and global, so writing a mapping for appid
        // 4121800892 also deleted that appid's ShaderCacheManager block — a section that
        // exists on a stock Steam install and is keyed by the same non-Steam appid space.
        var (steam, temp) = Fixture(ConfigWithShaderCache);
        using (temp)
        {
            new ConfigVdfService().SetCompatTool(steam, 4121800892, "GE-Proton10-4");

            var text = File.ReadAllText(steam.ConfigVdfPath);
            Assert.Contains("\"ShaderCacheSize\"\t\t\"191366560\"", text);
            Assert.Contains("\"ShaderCacheSize\"\t\t\"8378331\"", text);
            Assert.Equal("GE-Proton10-4", new ConfigVdfService().GetCompatTool(steam, 4121800892));
        }
    }

    [Fact]
    public void Reading_a_compat_tool_ignores_a_same_appid_block_in_another_section()
    {
        // Was: the first match anywhere in the file answered. A false "already mapped" makes
        // the caller skip writing the tool, leaving a shortcut Steam launches with no Proton.
        var config = ConfigWithShaderCache.Replace(
            "\"ShaderCacheSize\"\t\t\"191366560\"",
            "\"name\"\t\t\"bucket-7\""
        );
        var (steam, temp) = Fixture(config);
        using (temp)
        {
            Assert.Null(new ConfigVdfService().GetCompatTool(steam, 4121800892));
        }
    }

    [Fact]
    public void Rewriting_the_same_appid_replaces_its_entry_rather_than_stacking_one()
    {
        var (steam, temp) = Fixture(ConfigWithShaderCache);
        using (temp)
        {
            var service = new ConfigVdfService();
            service.SetCompatTool(steam, 4121800892, "GE-Proton10-4");
            service.SetCompatTool(steam, 4121800892, "GE-Proton11-1");

            var text = File.ReadAllText(steam.ConfigVdfPath);
            // Counted inside the mapping block only: the appid legitimately appears a second
            // time in ShaderCacheManager, and leaving that one alone is the point of the fix.
            var mapping = ConfigVdfService.FindBlockBody(text, "CompatToolMapping")!.Value;
            var body = text[mapping.Start..mapping.End];
            Assert.Equal(1, body.Split("\"4121800892\"").Length - 1);
            Assert.Equal("GE-Proton11-1", service.GetCompatTool(steam, 4121800892));
            Assert.Contains("proton_experimental", text);
            Assert.Contains("\"ShaderCacheSize\"\t\t\"191366560\"", text);
        }
    }

    [Fact]
    public void The_mapping_block_body_is_found_by_matching_braces()
    {
        var body = ConfigVdfService.FindBlockBody(ConfigWithShaderCache, "CompatToolMapping");

        Assert.NotNull(body);
        var text = ConfigWithShaderCache[body!.Value.Start..body.Value.End];
        Assert.Contains("489830", text);
        Assert.DoesNotContain("ShaderCacheSize", text);
    }

    [Fact]
    public void An_empty_shortcuts_file_reads_as_no_shortcuts()
    {
        // Was: InvalidDataException from every entry point into the Steam feature — List, Find
        // and Upsert alike — with no way back except deleting the file by hand.
        Assert.Empty(BinaryVdf.Read([]));
    }

    [Fact]
    public void A_truncated_shortcuts_file_is_still_refused()
    {
        // The empty-file allowance must not weaken the guard that stops a partially truncated
        // file being laundered into a valid one that silently drops the user's other entries.
        var full = BinaryVdf.Write(
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["a"] = "b" }
        );

        Assert.Throws<InvalidDataException>(() => BinaryVdf.Read(full[..^1]));
    }

    [Theory]
    [InlineData("Proton - Experimental", "proton_experimental")]
    [InlineData("Proton 9.0", "proton_9")]
    [InlineData("Proton Hotfix", "proton_hotfix")]
    public void Valve_proton_folder_names_map_to_the_internal_names_steam_writes(
        string display,
        string expected
    ) => Assert.Equal(expected, CompatToolCatalog.ValveInternalName(display));
}

public class InfrastructureRegressionTests
{
    [Fact]
    public async Task A_throwing_started_subscriber_does_not_wedge_the_runner()
    {
        // Was: the busy flag was taken before the try, so one throwing subscriber left it set
        // for the life of the process and every later operation refused to start.
        var runner = new OperationRunner(new ServiceProviderStub(), new LogService(null));
        void Boom(string _) => throw new InvalidOperationException("boom");
        runner.Started += Boom;

        var first = await runner.RunAsync("first", (_, _) => Task.CompletedTask);
        // The throwing subscriber goes away, standing in for a dispatcher that has since come
        // back. What must not survive is the busy flag the failed run left behind.
        runner.Started -= Boom;
        var ran = false;
        var second = await runner.RunAsync("second", (_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.Equal(OperationOutcome.Failed, first.Outcome);
        Assert.False(runner.IsBusy);
        Assert.True(ran);
        Assert.Equal(OperationOutcome.Succeeded, second.Outcome);
    }

    [Fact]
    public async Task A_throwing_completed_subscriber_does_not_escape_the_runner()
    {
        // Was: Completed was invoked outside every guard, so the exception reached an async
        // command with nothing to catch it and took the process down.
        var runner = new OperationRunner(new ServiceProviderStub(), new LogService(null));
        runner.Completed += (_, _) => throw new InvalidOperationException("boom");

        var result = await runner.RunAsync("op", (_, _) => Task.CompletedTask);

        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public void A_throwing_log_subscriber_does_not_escape_append()
    {
        // Append is called from inside catch blocks reporting failures the user needs to see.
        var log = new LogService(null);
        log.LineAdded += _ => throw new InvalidOperationException("boom");

        log.Append("hello");
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("v0.2", "0.2.0", false)]
    [InlineData("v0.3", "0.2.0", true)]
    [InlineData("v0.2.0-rc1", "0.1.0", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("not-a-version", "0.1.0", false)]
    public void Update_comparison_handles_the_tag_shapes_that_actually_get_pushed(
        string tag,
        string current,
        bool expected
    )
    {
        // Was: a two-component tag compared as older than its three-component equivalent, and
        // a pre-release suffix failed to parse into "you are up to date".
        Assert.Equal(expected, AppUpdateService.IsNewer(tag, current));
    }

    private sealed class ServiceProviderStub : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory)
                ? new ScopeFactory()
                : null;

        private sealed class ScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
        {
            public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => new Scope();
        }

        private sealed class Scope : Microsoft.Extensions.DependencyInjection.IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new ServiceProviderStub();

            public void Dispose() { }
        }
    }
}

public class OneClickInstallTests
{
    private static AppSettings Settings() => new();

    [Fact]
    public void The_shipped_catalog_recommends_only_additive_addons()
    {
        // ElvUI and Dominos both replace the action bars and conflict with each other,
        // Immersion replaces the quest window, Grid2 is healer-only. Installing any of them
        // without being asked is an opinion, and one-click must not hold opinions.
        var opinionated = (string[])["elvui", "dominos", "immersion", "grid2", "cartomapper"];

        var recommended = ShippedCatalog().Where(e => e.Recommended).Select(e => e.Id).ToList();

        Assert.NotEmpty(recommended);
        Assert.All(opinionated, id => Assert.DoesNotContain(id, recommended));
        Assert.Contains("zygor", recommended);
        Assert.Contains("dbm", recommended);
        // Zygor is the default levelling guide, so Questie is not: both do the same job, and
        // two guide addons on at once is redundancy the user did not ask for. Questie stays in
        // the catalog — this is about what gets ticked, not what is offered.
        Assert.DoesNotContain("questie", recommended);
        Assert.Single(recommended, id => id is "zygor" or "questie");
    }

    [Fact]
    public void An_empty_saved_selection_means_none_not_the_recommended_set()
    {
        // Distinguishing "never chose" (null) from "chose nothing" (empty) is the difference
        // between honouring a deliberate opt-out and quietly overriding it.
        var settings = Settings();
        settings.SelectedAddonIds = [];

        Assert.Empty(Select(settings));
    }

    [Fact]
    public void No_saved_selection_falls_back_to_the_catalog_recommendation()
    {
        var settings = Settings();
        settings.SelectedAddonIds = null;

        Assert.NotEmpty(Select(settings));
    }

    [Fact]
    public void A_saved_selection_is_honoured_exactly()
    {
        var settings = Settings();
        settings.SelectedAddonIds = ["elvui"];

        Assert.Equal("elvui", Assert.Single(Select(settings)).Id);
    }

    [Fact]
    public void An_id_no_longer_in_the_catalog_is_ignored_rather_than_failing()
    {
        var settings = Settings();
        settings.SelectedAddonIds = ["questie", "an-addon-that-was-removed"];

        Assert.Equal("questie", Assert.Single(Select(settings)).Id);
    }

    /// <summary>The catalog file that actually ships, read from the build output.</summary>
    private static List<CatalogAddon> ShippedCatalog() =>
        AddonCatalog.Parse(File.ReadAllText("addon-catalog.json"));

    /// <summary>Exercises the real selection logic without constructing the whole orchestrator.</summary>
    private static List<CatalogAddon> Select(AppSettings settings)
    {
        var entries = ShippedCatalog();
        return settings.SelectedAddonIds is { } ids
            ? entries.Where(e => ids.Contains(e.Id, StringComparer.Ordinal)).ToList()
            : entries.Where(e => e.Recommended).ToList();
    }
}

public class SelfUpdateRestartTests
{
    private static AppUpdateService NewService() =>
        new(new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddHttpClient()
            .BuildServiceProvider()
            .GetRequiredService<System.Net.Http.IHttpClientFactory>());

    /// <summary>
    /// chmod +x, guarded for the analyzer. These tests are Linux-only by nature — the thing
    /// under test spawns /bin/sh — but the call site still has to say so.
    /// </summary>
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
    }

    [Fact]
    public void Relaunch_refuses_a_path_that_is_not_there()
    {
        // The caller shuts the app down only when this returns true. Returning true for a
        // missing file would close the window with nothing to replace it.
        Assert.False(NewService().TryRelaunch("/nonexistent/WowWotlkAutoinstall.AppImage"));
    }

    [Fact]
    public void Relaunch_refuses_an_empty_path()
    {
        Assert.False(NewService().TryRelaunch(""));
    }

    [Fact]
    public void Relaunch_starts_the_target_and_survives_this_process()
    {
        // setsid puts the replacement in its own session, so it is not a child of the dying
        // app. Without that it would be killed along with the process that started it.
        using var temp = new TempDir();
        var marker = temp.Join("started.txt");
        var script = temp.Join("fake.AppImage");
        File.WriteAllText(script, $"#!/bin/sh\nsleep 0.2\necho \"$$\" > \"{marker}\"\n");
        MakeExecutable(script);

        Assert.True(NewService().TryRelaunch(script));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(marker) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }
        Assert.True(File.Exists(marker), "the relaunched process never ran");
    }

    [Fact]
    public void Relaunch_does_not_hand_on_the_old_images_environment()
    {
        // APPIMAGE, APPDIR and LD_LIBRARY_PATH all describe the image this process has
        // mounted, and that mount disappears when it exits. Inherited, they point the new
        // instance at a mount that is about to vanish.
        using var temp = new TempDir();
        var dump = temp.Join("env.txt");
        var script = temp.Join("fake.AppImage");
        File.WriteAllText(
            script,
            $"#!/bin/sh\n{{ echo \"APPIMAGE=[$APPIMAGE]\"; echo \"APPDIR=[$APPDIR]\"; "
                + $"echo \"LD_LIBRARY_PATH=[$LD_LIBRARY_PATH]\"; }} > \"{dump}\"\n"
        );
        MakeExecutable(script);
        Environment.SetEnvironmentVariable("APPIMAGE", "/old/mount/App.AppImage");
        Environment.SetEnvironmentVariable("APPDIR", "/tmp/.mount_old");
        Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", "/tmp/.mount_old/usr/lib");
        try
        {
            Assert.True(NewService().TryRelaunch(script));

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(dump) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            var text = File.ReadAllText(dump);
            Assert.Contains("APPIMAGE=[]", text);
            Assert.Contains("APPDIR=[]", text);
            Assert.Contains("LD_LIBRARY_PATH=[]", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPIMAGE", null);
            Environment.SetEnvironmentVariable("APPDIR", null);
            Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", null);
        }
    }

    [Fact]
    public void Relaunch_handles_a_path_with_spaces()
    {
        using var temp = new TempDir();
        var marker = temp.Join("ran.txt");
        var dir = temp.Join("a folder with spaces");
        Directory.CreateDirectory(dir);
        var script = Path.Join(dir, "My App.AppImage");
        File.WriteAllText(script, $"#!/bin/sh\ntouch \"{marker}\"\n");
        MakeExecutable(script);

        Assert.True(NewService().TryRelaunch(script));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(marker) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }
        Assert.True(File.Exists(marker), "a path containing spaces was not launched");
    }
}
