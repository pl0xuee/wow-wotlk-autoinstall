using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Client;
using WowWotlk.Gui.Services.Display;
using Xunit;

namespace WowWotlk.Tests;

public class WowConfigServiceTests
{
    private static WowConfigService NewService() => new(new LogService(null));

    private static DisplaySettings Fullscreen(int w, int h) =>
        new(new DisplayMode(w, h), Windowed: false);

    [Fact]
    public void Writes_a_config_for_a_client_that_has_never_run()
    {
        // The reason this exists: with no Config.wtf a 3.3.5a client opens at 800x600, and
        // fixing that from the in-game menu means finding the menu at 800x600 first.
        using var temp = new TempDir();

        var path = NewService().Apply(temp.Path, Fullscreen(2560, 1440));

        var text = File.ReadAllText(path);
        Assert.EndsWith(Path.Join("WTF", "Config.wtf"), path);
        Assert.Contains("SET gxResolution \"2560x1440\"", text);
        Assert.Contains("SET gxWindow \"0\"", text);
    }

    [Fact]
    public void Keeps_every_other_setting_in_the_file()
    {
        // Config.wtf accumulates everything the user has ever changed in the options menu.
        // Rewriting it wholesale to set two values would throw all of that away.
        const string existing =
            "SET locale \"enUS\"\nSET gxResolution \"800x600\"\nSET Sound_MusicVolume \"0.4\"\n"
            + "SET realmName \"Local\"\n";

        var updated = WowConfigService.SetValues(
            existing,
            WowConfigService.Values(Fullscreen(1920, 1080))
        );

        Assert.Contains("SET locale \"enUS\"", updated);
        Assert.Contains("SET Sound_MusicVolume \"0.4\"", updated);
        Assert.Contains("SET realmName \"Local\"", updated);
        Assert.Contains("SET gxResolution \"1920x1080\"", updated);
        Assert.DoesNotContain("800x600", updated);
    }

    [Fact]
    public void Replaces_every_occurrence_not_just_the_first()
    {
        // The client reads top to bottom and the last assignment wins, so replacing only the
        // first would leave the old resolution in force — the same trap as realmlist.
        const string existing = "SET gxResolution \"800x600\"\nSET gxResolution \"1024x768\"\n";

        var updated = WowConfigService.SetValues(
            existing,
            WowConfigService.Values(Fullscreen(1920, 1080))
        );

        Assert.DoesNotContain("800x600", updated);
        Assert.DoesNotContain("1024x768", updated);
        Assert.Equal(2, updated.Split("SET gxResolution \"1920x1080\"").Length - 1);
    }

    [Theory]
    [InlineData("SET gxResolution \"1920x1080\"")]
    [InlineData("set gxresolution \"1920x1080\"")]
    [InlineData("SET  gxResolution  1920x1080")]
    [InlineData("\tSET gxResolution \"1920x1080\"\t")]
    public void Tolerates_the_casing_and_quoting_repacks_actually_ship(string line)
    {
        // The keyword and key are case-insensitive to the client, and repacks vary on quoting.
        Assert.Equal("1920x1080", WowConfigService.ReadValue(line, "gxResolution"));
    }

    [Fact]
    public void Windowed_sets_maximize_too()
    {
        // Without gxMaximize a windowed client at the desktop's own resolution opens with its
        // title bar off-screen, where it cannot be dragged back.
        var values = WowConfigService.Values(new DisplaySettings(new DisplayMode(1920, 1080), true));

        Assert.Equal("1", values["gxWindow"]);
        Assert.Equal("1", values["gxMaximize"]);
    }

    [Fact]
    public void Reads_back_what_the_client_is_set_to()
    {
        using var temp = new TempDir();
        temp.Write("WTF/Config.wtf", "SET gxResolution \"1600x900\"\nSET gxWindow \"1\"\n");

        var read = NewService().Read(temp.Path);

        Assert.Equal(new DisplayMode(1600, 900), read?.Resolution);
        Assert.True(read?.Windowed);
    }

    [Fact]
    public void Reads_null_for_a_client_with_no_config_yet()
    {
        using var temp = new TempDir();

        Assert.Null(NewService().Read(temp.Path));
    }

    [Theory]
    [InlineData("1920x1080", 1920, 1080)]
    [InlineData("2560X1440", 2560, 1440)]
    [InlineData(" 1280x720 ", 1280, 720)]
    public void Parses_a_resolution(string value, int w, int h) =>
        Assert.Equal(new DisplayMode(w, h), WowConfigService.ParseMode(value));

    [Theory]
    [InlineData("")]
    [InlineData("fullscreen")]
    [InlineData("0x0")]
    [InlineData("1920")]
    public void Refuses_something_that_is_not_a_resolution(string value) =>
        Assert.Null(WowConfigService.ParseMode(value));
}

public class DisplayCatalogTests
{
    private static string Fixture(TempDir temp, string connector, string status, params string[] modes)
    {
        temp.Write(Path.Join(connector, "status"), status + "\n");
        temp.Write(Path.Join(connector, "modes"), string.Join("\n", modes) + "\n");
        return temp.Path;
    }

    [Fact]
    public void Lists_modes_from_connected_outputs_only()
    {
        using var temp = new TempDir();
        Fixture(temp, "card1-DP-1", "connected", "2560x1440", "1920x1080");
        Fixture(temp, "card1-HDMI-A-1", "disconnected", "1920x1080");

        var outputs = new DisplayCatalog(temp.Path, () => null).Scan();

        Assert.Equal("DP-1", Assert.Single(outputs).Connector);
    }

    [Fact]
    public void Offers_each_resolution_once_across_displays()
    {
        // A mode both monitors support is one choice, not two.
        using var temp = new TempDir();
        Fixture(temp, "card1-DP-1", "connected", "2560x1440", "1920x1080");
        Fixture(temp, "card1-DP-2", "connected", "1920x1080");

        var choices = new DisplayCatalog(temp.Path, () => null).Choices();

        Assert.Equal(2, choices.Count);
        Assert.Equal(2, choices.Single(c => c.Mode.ToString() == "1920x1080").Displays.Count);
    }

    [Fact]
    public void Puts_the_primary_displays_native_mode_first()
    {
        // The mode the user almost always wants, so it is what the picker defaults to.
        using var temp = new TempDir();
        Fixture(temp, "card1-DP-1", "connected", "3840x2160", "1920x1080");
        Fixture(temp, "card1-DP-2", "connected", "1920x1080");

        var choices = new DisplayCatalog(temp.Path, () => "DP-2 connected primary 1920x1080+0+0").Choices();

        Assert.Equal("1920x1080", choices[0].Mode.ToString());
        Assert.True(choices[0].IsPrimaryNative);
    }

    [Fact]
    public void Reports_nothing_rather_than_guessing_when_sysfs_is_absent()
    {
        // A machine whose displays cannot be read leaves the client's own default alone.
        Assert.Empty(new DisplayCatalog("/nonexistent/drm", () => null).Choices());
    }

    [Fact]
    public void Ignores_a_connected_output_with_no_modes()
    {
        // Writeback and virtual connectors report connected with nothing to show.
        using var temp = new TempDir();
        temp.Write("card1-Writeback-1/status", "connected\n");
        temp.Write("card1-Writeback-1/modes", "\n");

        Assert.Empty(new DisplayCatalog(temp.Path, () => null).Scan());
    }
}
