using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Client;
using Xunit;

namespace WowWotlk.Tests;

public class RealmlistServiceTests
{
    private static RealmlistService NewService() => new(new LogService(null));

    [Fact]
    public void Writes_every_locale_folder_it_finds()
    {
        using var temp = new TempDir();
        temp.Write("Data/enUS/realmlist.wtf", "set realmlist logon.somerepack.com\n");
        temp.Write("Data/deDE/realmlist.wtf", "set realmlist logon.somerepack.com\n");

        var written = NewService().Apply(temp.Path, "192.168.1.50");

        Assert.Equal(2, written.Count);
        Assert.Contains("set realmlist 192.168.1.50", File.ReadAllText(temp.Join("Data/enUS/realmlist.wtf")));
        Assert.Contains("set realmlist 192.168.1.50", File.ReadAllText(temp.Join("Data/deDE/realmlist.wtf")));
    }

    [Fact]
    public void Creates_the_file_when_the_client_has_none()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Join("Data", "enUS"));

        NewService().Apply(temp.Path, "127.0.0.1");

        Assert.Equal("set realmlist 127.0.0.1\n", File.ReadAllText(temp.Join("Data/enUS/realmlist.wtf")));
    }

    [Fact]
    public void Writes_a_root_realmlist_only_when_the_client_already_had_one()
    {
        using var temp = new TempDir();
        temp.Write("realmlist.wtf", "set realmlist old.example.com\n");
        temp.Write("Data/enUS/realmlist.wtf", "");

        NewService().Apply(temp.Path, "10.0.0.2");

        Assert.Contains("set realmlist 10.0.0.2", File.ReadAllText(temp.Join("realmlist.wtf")));
    }

    [Fact]
    public void Does_not_invent_a_root_realmlist()
    {
        using var temp = new TempDir();
        temp.Write("Data/enUS/realmlist.wtf", "");

        NewService().Apply(temp.Path, "10.0.0.2");

        Assert.False(File.Exists(temp.Join("realmlist.wtf")));
    }

    [Fact]
    public void Preserves_other_directives()
    {
        // The failure this guards: a client that also pins a patchlist stops patching when the
        // file is rewritten wholesale instead of line-replaced.
        const string existing = "set realmlist old.example.com\nset patchlist patch.example.com\n";

        var updated = RealmlistService.SetRealmlist(existing, "127.0.0.1");

        Assert.Contains("set realmlist 127.0.0.1", updated);
        Assert.Contains("set patchlist patch.example.com", updated);
        Assert.DoesNotContain("old.example.com", updated);
    }

    [Fact]
    public void Is_idempotent()
    {
        var once = RealmlistService.SetRealmlist("", "127.0.0.1");
        var twice = RealmlistService.SetRealmlist(once, "127.0.0.1");

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Appends_when_the_file_has_content_but_no_realmlist_line()
    {
        var updated = RealmlistService.SetRealmlist("set patchlist patch.example.com\n", "127.0.0.1");

        Assert.Contains("set patchlist patch.example.com", updated);
        Assert.Contains("set realmlist 127.0.0.1", updated);
    }

    [Fact]
    public void Reads_back_the_configured_address()
    {
        using var temp = new TempDir();
        temp.Write("Data/enUS/realmlist.wtf", "set realmlist wow.lan\n");

        Assert.Equal("wow.lan", NewService().Read(temp.Path));
    }

    [Fact]
    public void Clears_the_cache_folder()
    {
        // A stale Cache keeps offering the previous realm, which reads to the user as the
        // realmlist change having done nothing at all.
        using var temp = new TempDir();
        temp.Write("Data/enUS/realmlist.wtf", "");
        temp.Write("Cache/WDB/enUS/creaturecache.wdb", "stale");

        NewService().Apply(temp.Path, "127.0.0.1");

        Assert.False(Directory.Exists(temp.Join("Cache")));
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("  wow.lan  ", "wow.lan")]
    [InlineData("\"wow.lan\"", "wow.lan")]
    [InlineData("http://wow.lan", "wow.lan")]
    [InlineData("https://wow.lan/realm", "wow.lan")]
    // A port is part of the address, not noise: WoW only defaults to 3724 when none is given,
    // and a private server on another port is the normal case.
    [InlineData("wow.lan:3724", "wow.lan:3724")]
    [InlineData("203.0.113.10:1170", "203.0.113.10:1170")]
    [InlineData("http://203.0.113.10:1170", "203.0.113.10:1170")]
    [InlineData("203.0.113.10:1170/realm", "203.0.113.10:1170")]
    [InlineData("", "127.0.0.1")]
    public void Normalises_what_a_user_pastes_in(string input, string expected) =>
        Assert.Equal(expected, RealmlistService.Normalise(input));
}
