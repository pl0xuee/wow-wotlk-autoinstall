using WowWotlk.Gui.Services.Addons;
using Xunit;

namespace WowWotlk.Tests;

public class AddonTocTests
{
    [Fact]
    public void Reads_the_three_directives_the_ui_shows()
    {
        var (title, version, iface) = AddonArchive.ParseToc(
            "## Interface: 30300\n## Title: Questie\n## Version: 8.2.1\n## Notes: quests\n"
        );

        Assert.Equal("Questie", title);
        Assert.Equal("8.2.1", version);
        Assert.Equal("30300", iface);
    }

    [Fact]
    public void Strips_the_colour_escapes_wow_allows_in_a_title()
    {
        // A raw |cff00ff00Questie|r in the grid would leak the client's markup into the UI.
        var (title, _, _) = AddonArchive.ParseToc("## Title: |cff00ff00Questie|r\n");

        Assert.Equal("Questie", title);
    }

    [Theory]
    [InlineData("##Interface:30300")]
    [InlineData("##   interface   :   30300   ")]
    [InlineData("## INTERFACE: 30300")]
    public void Tolerates_the_spacing_and_casing_authors_actually_use(string line) =>
        Assert.Equal("30300", AddonArchive.ParseToc(line).Interface);

    [Fact]
    public void Returns_nulls_for_a_toc_that_declares_nothing() =>
        Assert.Equal((null, null, null), AddonArchive.ParseToc("# just a comment\n"));

    [Theory]
    [InlineData("30300", false)]
    [InlineData("30403", true)]
    [InlineData(null, false)]
    public void Flags_an_interface_that_is_not_3_3_5a(string? iface, bool expected)
    {
        var folder = new AddonFolder("X", "/x", "X", null, iface, true);

        Assert.Equal(expected, folder.InterfaceMismatch);
    }
}

public class AddonArchiveLayoutTests
{
    private static void Toc(TempDir temp, string relativeDir, string name) =>
        temp.Write(Path.Join(relativeDir, name + ".toc"), $"## Title: {name}\n## Interface: 30300\n");

    [Fact]
    public void Finds_a_single_addon_folder()
    {
        using var temp = new TempDir();
        Toc(temp, "Questie", "Questie");

        var found = AddonArchive.FindAddonFolders(temp.Path);

        Assert.Equal([temp.Join("Questie")], found);
    }

    [Fact]
    public void Finds_every_sibling_folder_a_zip_ships()
    {
        // AtlasLoot ships AtlasLoot and AtlasLoot_Data side by side; installing only the first
        // gives a client that loads the addon and then errors on its missing data module.
        using var temp = new TempDir();
        Toc(temp, "AtlasLoot", "AtlasLoot");
        Toc(temp, "AtlasLoot_Data", "AtlasLoot_Data");

        var found = AddonArchive.FindAddonFolders(temp.Path);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Descends_through_a_junk_wrapper_folder()
    {
        // A GitHub zipball unpacks as Questie-master/Questie/…; installing the wrapper would
        // put the addon at Interface/AddOns/Questie-master, where the client never finds it.
        using var temp = new TempDir();
        Toc(temp, "Questie-master/Questie", "Questie");

        var found = AddonArchive.FindAddonFolders(temp.Path);

        Assert.Equal([temp.Join("Questie-master", "Questie")], found);
    }

    [Fact]
    public void Uses_the_root_itself_when_the_toc_is_there()
    {
        using var temp = new TempDir();
        temp.Write("Questie.toc", "## Title: Questie\n");

        Assert.Equal([temp.Path], AddonArchive.FindAddonFolders(temp.Path));
    }

    [Fact]
    public void Finds_nothing_in_a_zip_with_no_toc()
    {
        // This is what a user gets for pasting a link to a screenshot, and it must be a clear
        // "no addon here" rather than an empty folder appearing in Interface/AddOns.
        using var temp = new TempDir();
        temp.Write("docs/readme.md", "hello");

        Assert.Empty(AddonArchive.FindAddonFolders(temp.Path));
    }

    [Fact]
    public void Stops_descending_past_the_depth_bound()
    {
        using var temp = new TempDir();
        Toc(temp, "a/b/c/d/Questie", "Questie");

        Assert.Empty(AddonArchive.FindAddonFolders(temp.Path));
    }
}

public class AddonScannerTests
{
    [Fact]
    public void Reports_enabled_and_disabled_folders_together()
    {
        using var temp = new TempDir();
        temp.Write("Interface/AddOns/Questie/Questie.toc", "## Title: Questie\n## Interface: 30300\n");
        temp.Write("Interface/AddOns.disabled/Recount/Recount.toc", "## Title: Recount\n## Interface: 30300\n");

        var found = new AddonScanner().Scan(temp.Path);

        Assert.Equal(2, found.Count);
        Assert.True(found.Single(f => f.Name == "Questie").Enabled);
        Assert.False(found.Single(f => f.Name == "Recount").Enabled);
    }

    [Fact]
    public void Leaves_the_stock_ui_modules_out()
    {
        // Blizzard_* are the client's own UI, not something a user installed or can remove.
        using var temp = new TempDir();
        temp.Write("Interface/AddOns/Blizzard_AuctionUI/Blizzard_AuctionUI.toc", "## Title: Auction\n");
        temp.Write("Interface/AddOns/Questie/Questie.toc", "## Title: Questie\n");

        var found = new AddonScanner().Scan(temp.Path);

        Assert.Equal("Questie", Assert.Single(found).Name);
    }

    [Fact]
    public void Returns_nothing_for_a_client_with_no_addons_folder()
    {
        using var temp = new TempDir();

        Assert.Empty(new AddonScanner().Scan(temp.Path));
    }
}
