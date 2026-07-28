using System.IO.Compression;
using WowWotlk.Gui.Services;
using WowWotlk.Gui.Services.Client;
using Xunit;

namespace WowWotlk.Tests;

public class ClientArchiveExtractorTests
{
    private static ClientArchiveExtractor NewExtractor() => new(new LogService(null));

    private static string MakeZip(TempDir temp, params (string Name, string Body)[] entries)
    {
        var path = temp.Join("archive.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, body) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(body);
        }
        return path;
    }

    [Fact]
    public async Task Extracts_entries_and_reports_progress()
    {
        using var temp = new TempDir();
        var zip = MakeZip(temp, ("Wow.exe", "MZ"), ("Data/enUS/realmlist.wtf", "set realmlist 127.0.0.1"));
        var dest = temp.Join("out");
        var reports = new List<ExtractProgress>();

        await NewExtractor().ExtractAsync(zip, dest, new Progress<ExtractProgress>(reports.Add));

        Assert.True(File.Exists(Path.Join(dest, "Wow.exe")));
        Assert.Equal("set realmlist 127.0.0.1", File.ReadAllText(Path.Join(dest, "Data/enUS/realmlist.wtf")));
    }

    [Fact]
    public async Task Refuses_an_entry_that_escapes_the_destination()
    {
        // Zip slip: without the guard, an entry named ../.. writes outside the folder the user
        // chose — which for a 16 GiB archive from a third party is not a theoretical concern.
        using var temp = new TempDir();
        var zip = MakeZip(temp, ("../escaped.txt", "pwned"));
        var dest = temp.Join("out");

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => NewExtractor().ExtractAsync(zip, dest)
        );

        Assert.Contains("escapes the destination", error.Message);
        Assert.False(File.Exists(temp.Join("escaped.txt")));
    }

    [Fact]
    public async Task Does_not_treat_a_sibling_with_a_shared_prefix_as_an_escape()
    {
        // /games/wow-old must not count as inside /games/wow, and /games/wow itself must not
        // be rejected for sharing its own prefix.
        using var temp = new TempDir();
        var zip = MakeZip(temp, ("Interface/AddOns/Questie/Questie.toc", "## Title: Questie"));
        var dest = temp.Join("wow");
        Directory.CreateDirectory(temp.Join("wow-old"));

        await NewExtractor().ExtractAsync(zip, dest);

        Assert.True(File.Exists(Path.Join(dest, "Interface/AddOns/Questie/Questie.toc")));
    }

    [Fact]
    public async Task Stops_when_cancelled()
    {
        using var temp = new TempDir();
        var zip = MakeZip(temp, ("a.txt", "a"), ("b.txt", "b"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewExtractor().ExtractAsync(zip, temp.Join("out"), null, cts.Token)
        );
    }
}
