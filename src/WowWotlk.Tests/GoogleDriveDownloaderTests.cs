using System.Net;
using System.Net.Http;
using WowWotlk.Gui.Services.Client;
using Xunit;

namespace WowWotlk.Tests;

public class GoogleDriveDownloaderTests
{
    [Fact]
    public void Builds_a_url_that_skips_the_virus_scan_interstitial()
    {
        // Without confirm=t, Drive serves an HTML "can't scan this file" page for anything
        // over ~100 MB rather than the bytes.
        var url = GoogleDriveDownloader.BuildUrl("1AbCdEfGhIjKlMnOpQrStUvWxYz012345");

        Assert.Contains("id=1AbCdEfGhIjKlMnOpQrStUvWxYz012345", url);
        Assert.Contains("confirm=t", url);
        Assert.Contains("export=download", url);
    }

    [Fact]
    public void Escapes_a_file_id_so_it_cannot_inject_query_parameters()
    {
        var url = GoogleDriveDownloader.BuildUrl("abc&export=evil");

        Assert.DoesNotContain("&export=evil", url);
    }

    [Fact]
    public void Rejects_an_html_body()
    {
        // The quota failure mode: Drive answers HTTP 200 with an error page. A downloader that
        // trusts the status code writes a web page to disk and calls it a game client.
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html>quota</html>"),
        };
        response.Content.Headers.ContentType = new("text/html");

        var error = Assert.Throws<InvalidDataException>(
            () => GoogleDriveDownloader.EnsureNotAnErrorPage(response)
        );

        Assert.Contains("quota", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_a_binary_body()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x50, 0x4B, 0x03, 0x04]),
        };
        response.Content.Headers.ContentType = new("application/zip");

        GoogleDriveDownloader.EnsureNotAnErrorPage(response);
    }

    [Fact]
    public void Accepts_a_body_with_no_content_type()
    {
        // Absent header is not evidence of an error page, and refusing here would block a
        // download that would have worked.
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x50, 0x4B]),
        };
        response.Content.Headers.ContentType = null;

        GoogleDriveDownloader.EnsureNotAnErrorPage(response);
    }

    [Theory]
    [InlineData(17_675_893_897L, "16.5 GiB")]
    [InlineData(5L * 1024 * 1024, "5.0 MiB")]
    [InlineData(512L, "512 B")]
    public void Formats_sizes_the_way_a_user_reads_them(long bytes, string expected) =>
        Assert.Equal(expected, GoogleDriveDownloader.Human(bytes));
}
