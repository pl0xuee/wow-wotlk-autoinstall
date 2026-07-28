using WowWotlk.Gui.Services.Steam;
using Xunit;

namespace WowWotlk.Tests;

public class LaunchOptionsTests
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void A_client_under_home_needs_no_mounts() =>
        Assert.Equal(
            "%command%",
            SteamIntegrationService.BuildLaunchOptions([Path.Join(Home, "Games", "WoW-WotLK")])
        );

    [Fact]
    public void A_client_on_another_drive_is_mounted_into_the_prefix()
    {
        // Without STEAM_COMPAT_MOUNTS, Proton cannot see a client on a second drive and the
        // shortcut launches into nothing with no useful error.
        var options = SteamIntegrationService.BuildLaunchOptions(["/mnt/games/WoW-WotLK"]);

        Assert.Equal("STEAM_COMPAT_MOUNTS=\"/mnt/games/WoW-WotLK\" %command%", options);
    }

    [Fact]
    public void Home_itself_counts_as_inside_home() =>
        Assert.Equal("%command%", SteamIntegrationService.BuildLaunchOptions([Home]));

    [Fact]
    public void A_sibling_of_home_is_not_inside_home()
    {
        // /home/bob2 must not be treated as under /home/bob.
        var options = SteamIntegrationService.BuildLaunchOptions([Home + "2"]);

        Assert.Contains(Home + "2", options);
    }

    [Fact]
    public void Duplicate_paths_are_listed_once()
    {
        var options = SteamIntegrationService.BuildLaunchOptions(
            ["/mnt/games/WoW", "/mnt/games/WoW"]
        );

        Assert.Equal("STEAM_COMPAT_MOUNTS=\"/mnt/games/WoW\" %command%", options);
    }

    [Fact]
    public void Every_form_ends_in_the_command_placeholder()
    {
        // Steam substitutes the real command for %command%; an option string without it
        // silently replaces the launch rather than wrapping it.
        Assert.EndsWith("%command%", SteamIntegrationService.BuildLaunchOptions([]));
        Assert.EndsWith("%command%", SteamIntegrationService.BuildLaunchOptions(["/mnt/x"]));
    }
}

public class WotlkProtonTests
{
    private static CompatTool Tool(string name, int? runtime = null) =>
        new(name, name, "/tools/" + name, runtime);

    [Fact]
    public void Prefers_ge_over_valve_proton()
    {
        var ordered = WotlkProton.Order([Tool("proton_9"), Tool("GE-Proton10-4")]).ToList();

        Assert.Equal("GE-Proton10-4", ordered[0].InternalName);
    }

    [Fact]
    public void Prefers_the_newest_build_within_a_tier()
    {
        var ordered = WotlkProton
            .Order([Tool("GE-Proton9-20"), Tool("GE-Proton10-4"), Tool("GE-Proton10-12")])
            .ToList();

        Assert.Equal("GE-Proton10-12", ordered[0].InternalName);
    }

    [Fact]
    public void Survives_a_date_stamped_build_name()
    {
        // A build named GE-Proton-20250101120000 overflows int and would otherwise throw,
        // taking the whole Steam page down with it.
        var ordered = WotlkProton.Order([Tool("GE-Proton-20250101120000"), Tool("GE-Proton10-4")]).ToList();

        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public void Skips_a_build_whose_runtime_is_not_installed()
    {
        var selection = WotlkProton.Select(
            [Tool("GE-Proton10-4", 1628350), Tool("proton_9")],
            appId => appId != 1628350,
            null
        );

        Assert.Equal("proton_9", selection.Tool?.InternalName);
        Assert.Equal("GE-Proton10-4", selection.SubstitutedFor?.InternalName);
    }

    [Fact]
    public void Honours_a_pin_even_when_it_is_not_the_top_ranked_build()
    {
        // 3.3.5a pins nothing, so a deliberate choice has no compatibility rule to lose to.
        var selection = WotlkProton.Select(
            [Tool("GE-Proton10-4"), Tool("proton_9")],
            _ => true,
            "proton_9"
        );

        Assert.Equal("proton_9", selection.Tool?.InternalName);
        Assert.Null(selection.SubstitutedFor);
    }

    [Fact]
    public void Falls_back_when_the_pinned_build_is_gone()
    {
        var selection = WotlkProton.Select([Tool("GE-Proton10-4")], _ => true, "GE-Proton9-1");

        Assert.Equal("GE-Proton10-4", selection.Tool?.InternalName);
    }

    [Fact]
    public void Reports_nothing_usable_when_no_runtime_is_installed()
    {
        var selection = WotlkProton.Select([Tool("GE-Proton10-4", 1628350)], _ => false, null);

        Assert.Null(selection.Tool);
    }

    [Fact]
    public void Names_the_missing_runtime_so_the_message_says_what_to_install()
    {
        var reason = WotlkProton.SubstitutionReason(Tool("GE-Proton10-4", 1628350), _ => false);

        Assert.Contains("sniper", reason);
    }
}

public class BinaryVdfTests
{
    [Fact]
    public void Round_trips_a_shortcut_entry()
    {
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["shortcuts"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["0"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["appid"] = -123456789,
                    ["AppName"] = "World of Warcraft 3.3.5a",
                    ["Exe"] = "\"/home/bob/Games/WoW-WotLK/Wow.exe\"",
                    ["LastPlayTime"] = 0,
                },
            },
        };

        var read = BinaryVdf.Read(BinaryVdf.Write(root));
        var entry = (Dictionary<string, object>)
            ((Dictionary<string, object>)read["shortcuts"])["0"];

        Assert.Equal("World of Warcraft 3.3.5a", entry["AppName"]);
        Assert.Equal(-123456789, entry["appid"]);
    }

    [Fact]
    public void Rejects_a_truncated_file()
    {
        // Laundering a corrupt shortcuts.vdf into a valid one silently drops every entry past
        // the truncation point — i.e. the rest of the user's non-Steam library.
        var full = BinaryVdf.Write(
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["a"] = "b" }
        );

        Assert.Throws<InvalidDataException>(() => BinaryVdf.Read(full[..^1]));
    }
}
