using WowWotlk.Gui.Models;
using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Steam;
using Xunit;

namespace WowWotlk.Tests;

public class PreflightSpaceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Passes_with_room_to_spare() =>
        Assert.Equal(
            CheckState.Ok,
            PreflightService.BuildSpaceCheck("Disk space", 100 * Gb, 45 * Gb, "/").State
        );

    [Fact]
    public void Fails_when_the_drive_is_too_small() =>
        Assert.Equal(
            CheckState.Fail,
            PreflightService.BuildSpaceCheck("Disk space", 10 * Gb, 45 * Gb, "/").State
        );

    [Fact]
    public void Warns_rather_than_blocks_a_re_run_over_an_existing_client()
    {
        // The requirement describes a fresh install. Demanding room for a second copy would
        // block the re-run and push the user into deleting the client they want repaired.
        var check = PreflightService.BuildSpaceCheck(
            "Disk space", 10 * Gb, 45 * Gb, "/", existingInstall: true
        );

        Assert.Equal(CheckState.Warn, check.State);
    }

    [Fact]
    public void Still_fails_a_re_run_with_no_headroom_at_all() =>
        Assert.Equal(
            CheckState.Fail,
            PreflightService.BuildSpaceCheck(
                "Disk space", 512 * 1024 * 1024, 45 * Gb, "/", existingInstall: true
            ).State
        );

    [Theory]
    [InlineData("/home/bob/Games", "/home/bob", true)]
    [InlineData("/home/bob", "/home/bob", true)]
    [InlineData("/home/bob2", "/home/bob", false)]
    [InlineData("/mnt/Games2", "/mnt/Games", false)]
    public void Treats_directory_boundaries_as_boundaries(string path, string root, bool expected) =>
        Assert.Equal(expected, PreflightService.IsUnder(path, root));

    [Theory]
    [InlineData("/home/bob/Games/WoW")]
    [InlineData("/mnt/second drive/WoW")]
    public void Accepts_paths_that_survive_a_vdf_round_trip(string dir) =>
        Assert.Null(PreflightService.PathProblem(dir));

    [Theory]
    [InlineData("/home/bob/\"quoted\"")]
    [InlineData("/home/bob/a:b")]
    public void Rejects_paths_steam_launch_options_cannot_represent(string dir) =>
        Assert.NotNull(PreflightService.PathProblem(dir));
}

public class PreflightSourceTests
{
    [Fact]
    public void Fails_a_zip_source_with_no_file_chosen()
    {
        var settings = new AppSettings { ClientSource = ClientSource.LocalZip, LocalZipPath = null };

        Assert.Equal(
            CheckState.Fail,
            PreflightService.SourceCheck(ClientSource.LocalZip, settings).State
        );
    }

    [Fact]
    public void Fails_a_zip_source_pointing_at_a_missing_file()
    {
        var settings = new AppSettings
        {
            ClientSource = ClientSource.LocalZip,
            LocalZipPath = "/nonexistent/client.zip",
        };

        Assert.Equal(
            CheckState.Fail,
            PreflightService.SourceCheck(ClientSource.LocalZip, settings).State
        );
    }

    [Fact]
    public void Fails_a_folder_source_with_no_client_in_it()
    {
        using var temp = new TempDir();
        temp.Write("readme.txt");
        var settings = new AppSettings
        {
            ClientSource = ClientSource.ExistingFolder,
            ExistingClientPath = temp.Path,
        };

        var check = PreflightService.SourceCheck(ClientSource.ExistingFolder, settings);

        Assert.Equal(CheckState.Fail, check.State);
        Assert.Contains("Wow.exe", check.Detail);
    }

    [Fact]
    public void Passes_a_folder_source_and_reports_the_resolved_client_root()
    {
        using var temp = new TempDir();
        temp.Write("World of Warcraft 3.3.5a/Wow.exe");
        var settings = new AppSettings
        {
            ClientSource = ClientSource.ExistingFolder,
            ExistingClientPath = temp.Path,
        };

        var check = PreflightService.SourceCheck(ClientSource.ExistingFolder, settings);

        Assert.Equal(CheckState.Ok, check.State);
        Assert.Equal(temp.Join("World of Warcraft 3.3.5a"), check.Detail);
    }

    [Fact]
    public void Fails_the_drive_source_when_the_file_id_is_cleared()
    {
        // The shipped build carries an id, but the Settings box can be emptied — clearing it to
        // paste a new one, most obviously. Say so up front rather than starting a download of
        // nothing.
        var check = PreflightService.SourceCheck(
            ClientSource.GoogleDrive,
            new AppSettings { DriveFileId = "" }
        );

        Assert.Equal(CheckState.Fail, check.State);
        Assert.Contains("Settings", check.Detail);
    }

    [Fact]
    public void Passes_the_drive_source_on_a_fresh_install()
    {
        // Nothing typed in, and the Install page's button is live: that is the whole point of
        // shipping the id.
        Assert.Equal(
            CheckState.Ok,
            PreflightService.SourceCheck(ClientSource.GoogleDrive, new AppSettings()).State
        );
    }

    [Fact]
    public void Passes_the_drive_source_once_a_file_id_is_set()
    {
        var settings = new AppSettings { DriveFileId = "1AbCdEfGhIjKlMnOpQrStUvWxYz012345" };

        Assert.Equal(
            CheckState.Ok,
            PreflightService.SourceCheck(ClientSource.GoogleDrive, settings).State
        );
    }
}

public class PreflightProtonTests
{
    private static CompatTool Tool(string name, int? runtime = null) =>
        new(name, name, "/tools/" + name, runtime);

    [Fact]
    public void Fails_when_nothing_is_installed() =>
        Assert.Equal(CheckState.Fail, PreflightService.ProtonCheck([], _ => true, null).State);

    [Fact]
    public void Passes_with_a_ge_build() =>
        Assert.Equal(
            CheckState.Ok,
            PreflightService.ProtonCheck([Tool("GE-Proton10-4")], _ => true, null).State
        );

    [Fact]
    public void Warns_when_it_had_to_substitute_for_a_missing_runtime()
    {
        // A build whose Steam Linux Runtime is absent cannot launch at all; discovering that
        // when the user clicks Play and gets nothing is the failure this avoids.
        var tools = new List<CompatTool> { Tool("GE-Proton10-4", 1628350), Tool("proton_9") };

        var check = PreflightService.ProtonCheck(tools, appId => appId != 1628350, null);

        Assert.Equal(CheckState.Warn, check.State);
        Assert.Contains("Steam Linux Runtime", check.Detail);
    }

    [Fact]
    public void Fails_when_every_build_is_missing_its_runtime()
    {
        var tools = new List<CompatTool> { Tool("GE-Proton10-4", 1628350) };

        Assert.Equal(CheckState.Fail, PreflightService.ProtonCheck(tools, _ => false, null).State);
    }

    [Fact]
    public void Honours_a_pinned_build_that_can_run()
    {
        var tools = new List<CompatTool> { Tool("GE-Proton10-4"), Tool("proton_9") };

        var check = PreflightService.ProtonCheck(tools, _ => true, "proton_9");

        Assert.Equal(CheckState.Ok, check.State);
        Assert.Contains("proton_9", check.Detail);
    }
}
