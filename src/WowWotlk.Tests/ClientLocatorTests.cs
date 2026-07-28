using WowWotlk.Gui.Services.Client;
using Xunit;

namespace WowWotlk.Tests;

public class ClientLocatorTests
{
    [Fact]
    public void Finds_a_client_at_the_folder_it_was_given()
    {
        using var temp = new TempDir();
        temp.Write("Wow.exe");

        Assert.Equal(Path.GetFullPath(temp.Path), ClientLocator.Find(temp.Path));
    }

    [Fact]
    public void Finds_a_client_under_a_wrapper_folder()
    {
        // The Drive zip may unpack under its own name rather than flat; the folder the user
        // chose is then the parent, not the client.
        using var temp = new TempDir();
        temp.Write("World of Warcraft 3.3.5a/Wow.exe");

        Assert.Equal(temp.Join("World of Warcraft 3.3.5a"), ClientLocator.Find(temp.Path));
    }

    [Fact]
    public void Matches_the_executable_name_case_insensitively()
    {
        // A zip built on Windows preserves whatever casing it was stored with, and a
        // case-sensitive match would reject a perfectly good client.
        using var temp = new TempDir();
        temp.Write("WOW.EXE");

        Assert.Equal(Path.GetFullPath(temp.Path), ClientLocator.Find(temp.Path));
    }

    [Fact]
    public void Returns_null_when_there_is_no_client()
    {
        using var temp = new TempDir();
        temp.Write("Data/enUS/patch.MPQ");

        Assert.Null(ClientLocator.Find(temp.Path));
    }

    [Fact]
    public void Returns_null_for_a_missing_or_blank_path()
    {
        Assert.Null(ClientLocator.Find(null));
        Assert.Null(ClientLocator.Find("   "));
        Assert.Null(ClientLocator.Find("/nonexistent/path/that/does/not/exist"));
    }

    [Fact]
    public void Stops_looking_below_the_depth_limit()
    {
        using var temp = new TempDir();
        temp.Write("a/b/c/d/Wow.exe");

        Assert.Null(ClientLocator.Find(temp.Path));
    }

    [Fact]
    public void Picks_the_same_client_every_run_when_a_folder_holds_two()
    {
        // Enumeration order is not guaranteed by the filesystem, so the search sorts; without
        // that, two runs against the same folder can resolve to different clients.
        using var temp = new TempDir();
        temp.Write("zeta/Wow.exe");
        temp.Write("alpha/Wow.exe");

        Assert.Equal(temp.Join("alpha"), ClientLocator.Find(temp.Path));
    }

    [Fact]
    public void Exposes_the_executable_path_with_its_on_disk_casing()
    {
        using var temp = new TempDir();
        temp.Write("WoW.EXE");

        Assert.Equal(temp.Join("WoW.EXE"), ClientLocator.FindExe(temp.Path));
    }
}
