using WowWotlk.Gui.Services.Client;
using WowWotlk.Gui.Services.Patches;
using Xunit;

namespace WowWotlk.Tests;

public class ClientLocaleTests
{
    [Fact]
    public void Detects_the_locale_from_the_data_folder()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Join("Data", "deDE"));

        Assert.Equal("deDE", ClientLocales.Detect(temp.Path));
    }

    [Fact]
    public void Prefers_english_when_a_repack_shipped_two_languages()
    {
        // The patch and the server almost certainly assume English, and picking by name order
        // would land on deDE purely because d sorts before e.
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Join("Data", "deDE"));
        Directory.CreateDirectory(temp.Join("Data", "enUS"));

        Assert.Equal("enUS", ClientLocales.Detect(temp.Path));
    }

    [Fact]
    public void Falls_back_to_enUS_for_a_client_with_no_locale_folder()
    {
        using var temp = new TempDir();

        Assert.Equal("enUS", ClientLocales.Detect(temp.Path));
    }

    [Fact]
    public void Ignores_folders_that_are_not_locales()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Join("Data", "Textures"));
        Directory.CreateDirectory(temp.Join("Data", "frFR"));

        Assert.Equal(["frFR"], ClientLocales.Present(temp.Path));
    }
}

public class PatchCatalogTests
{
    private static List<ClientPatch> Shipped() =>
        PatchCatalog.Parse(File.ReadAllText("patch-catalog.json"));

    [Fact]
    public void The_shipped_catalog_parses_and_has_the_dungeon_maps()
    {
        var entries = Shipped();

        var wdm = Assert.Single(entries, e => e.Id == "wdm-maps");
        Assert.Equal("Trimitor/WDM-patch", wdm.Repo);
        Assert.True(wdm.Recommended);
        Assert.NotNull(wdm.Homepage);
    }

    [Theory]
    [InlineData("enUS", "patch-enUS-M.MPQ")]
    [InlineData("deDE", "patch-deDE-M.MPQ")]
    [InlineData("ruRU", "patch-ruRU-M.MPQ")]
    public void The_asset_name_follows_the_client_language(string locale, string expected)
    {
        // The engine only reads a patch from the folder matching its own language, so the
        // wrong file is not a broken install — it is a silently absent one.
        var wdm = Shipped().Single(e => e.Id == "wdm-maps");

        Assert.Equal(expected, wdm.AssetFor(locale));
    }
}

public class InstalledPatchStoreTests
{
    private static InstalledPatch Patch(string id, string path) =>
        new(id, id, "enUS", path, "1", DateTimeOffset.UnixEpoch);

    [Fact]
    public void One_file_belongs_to_one_patch()
    {
        // Two records naming the same file would make removal ambiguous, and the second
        // install genuinely did replace the first.
        var store = new InstalledPatchStore();
        store.Record(Patch("a", "Data/enUS/patch-enUS-M.MPQ"));
        store.Record(Patch("b", "Data/enUS/patch-enUS-M.MPQ"));

        Assert.Equal("b", Assert.Single(store.All).Id);
    }

    [Fact]
    public void Re_recording_the_same_id_replaces_rather_than_duplicates()
    {
        var store = new InstalledPatchStore();
        store.Record(Patch("wdm-maps", "Data/enUS/patch-enUS-M.MPQ"));
        store.Record(Patch("wdm-maps", "Data/deDE/patch-deDE-M.MPQ"));

        Assert.Equal("Data/deDE/patch-deDE-M.MPQ", Assert.Single(store.All).RelativePath);
    }

    [Fact]
    public void Forget_matches_the_id_exactly()
    {
        var store = new InstalledPatchStore();
        store.Record(Patch("wdm-maps", "Data/enUS/patch-enUS-M.MPQ"));

        store.Forget("WDM-MAPS");

        Assert.Single(store.All);
    }
}
