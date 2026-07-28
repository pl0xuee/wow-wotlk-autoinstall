using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace WowWotlk.Gui.Services.Client;

/// <summary>Bytes fetched so far and the total the server reported, for a progress bar.</summary>
public sealed record DownloadProgress(long Downloaded, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Downloaded / Total, 0, 1) : 0;
}

/// <summary>
/// Resumable download of one public Google Drive file.
///
/// Two Drive-specific behaviours drive this design. Files over ~100 MB serve an HTML
/// "can't scan for viruses" interstitial instead of the bytes, bypassed with confirm=t. And
/// when the per-file daily download quota is exhausted, Drive answers HTTP 200 with an HTML
/// error page — so a downloader that trusts the status code happily writes 2 KB of HTML to
/// disk and calls it a 16 GiB game client. Both are caught here rather than an hour later
/// when the unzip fails.
/// </summary>
public class GoogleDriveDownloader(IHttpClientFactory hcf, LogService log)
{
    /// <summary>The file name a Drive download lands under, before <see cref="TempSuffix"/> is applied.</summary>
    public const string ClientZipName = "World of Warcraft 3.3.5a.zip";

    private const string TempSuffix = ".part";

    public static string BuildUrl(string fileId) =>
        $"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(fileId)}&export=download&confirm=t";

    /// <summary>
    /// Downloads to <paramref name="destinationPath"/>, resuming a previous <c>.part</c> file
    /// when one is there. Returns the destination path.
    ///
    /// <paramref name="expectedBytes"/> is checked against the finished file; zero skips the
    /// check. A file already present at full expected size is left alone — re-running an
    /// install must not re-download 16 GiB.
    /// </summary>
    public async Task<string> DownloadAsync(
        string fileId,
        string destinationPath,
        long expectedBytes,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var partial = destinationPath + TempSuffix;

        if (File.Exists(destinationPath))
        {
            var have = new FileInfo(destinationPath).Length;
            if (expectedBytes <= 0 || have == expectedBytes)
            {
                log.Append($"Client zip already downloaded ({Human(have)}); skipping download.");
                progress?.Report(new DownloadProgress(have, have));
                return destinationPath;
            }
            // Moved aside, not deleted. The size can disagree simply because the configured
            // expected size is wrong for this upload, and the file may be one the user
            // downloaded by hand over a browser after hitting the daily quota — deleting it
            // before knowing a replacement can even be fetched destroys 16 GiB and a day's
            // quota in one step.
            log.Append(
                $"Existing {Path.GetFileName(destinationPath)} is {Human(have)}, expected "
                    + $"{Human(expectedBytes)} — setting it aside as {Path.GetFileName(partial)} to resume from."
            );
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
            File.Move(destinationPath, partial);
        }

        var resumeFrom = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        // At exactly the expected size the .part is the finished download, not a bad file:
        // promote it rather than fetching 16 GiB again.
        if (expectedBytes > 0 && resumeFrom == expectedBytes)
        {
            File.Move(partial, destinationPath, overwrite: true);
            log.Append($"Found a complete download already on disk ({Human(resumeFrom)}).");
            progress?.Report(new DownloadProgress(resumeFrom, resumeFrom));
            return destinationPath;
        }
        if (expectedBytes > 0 && resumeFrom > expectedBytes)
        {
            log.Append($"Discarding a partial download that is already {Human(resumeFrom)}.");
            File.Delete(partial);
            resumeFrom = 0;
        }

        var http = hcf.CreateClient();
        // A 16 GiB download over a slow link far outlives the 100s default.
        http.Timeout = Timeout.InfiniteTimeSpan;

        var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(fileId));
        if (resumeFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            log.Append($"Resuming download at {Human(resumeFrom)}.");
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 416 means the .part is already at or past the end of the real file — which happens
        // whenever the configured expected size is larger than the actual upload. Retrying the
        // same range would fail identically every run, so the partial file is the problem and
        // has to go; without this the install is stuck for good behind a raw HTTP error.
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && resumeFrom > 0)
        {
            File.Delete(partial);
            throw new InvalidDataException(
                $"The partial download was {Human(resumeFrom)}, which is past the end of the file "
                    + "on Google Drive. It has been discarded — run the install again to download "
                    + "from the start. If this repeats, the expected zip size in Settings is "
                    + "larger than your upload; correct it or set it to 0."
            );
        }
        response.EnsureSuccessStatusCode();

        // Asked to resume but served the whole file: start over rather than append the first
        // N bytes onto themselves and produce a corrupt archive.
        if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            log.Append("Server ignored the resume request; starting the download from the beginning.");
            resumeFrom = 0;
        }

        EnsureNotAnErrorPage(response);

        var total = resumeFrom + (response.Content.Headers.ContentLength ?? 0);
        if (expectedBytes > 0)
        {
            total = expectedBytes;
        }

        await using (
            var file = new FileStream(
                partial,
                resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1 << 20
            )
        )
        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                var done = resumeFrom;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(new DownloadProgress(done, total));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        var finalSize = new FileInfo(partial).Length;
        if (expectedBytes > 0 && finalSize != expectedBytes)
        {
            throw new InvalidDataException(
                $"Downloaded {Human(finalSize)} but expected {Human(expectedBytes)}. The partial "
                    + $"file was kept at {partial} — run the install again to resume it."
            );
        }

        File.Move(partial, destinationPath, overwrite: true);
        log.Append($"Downloaded {Path.GetFileName(destinationPath)} ({Human(finalSize)}).");
        return destinationPath;
    }

    /// <summary>
    /// Drive answers a quota-exceeded or no-longer-shared link with an HTML page and HTTP 200.
    /// Catching it on the content type turns a silently corrupt download into a message that
    /// says what to do.
    /// </summary>
    internal static void EnsureNotAnErrorPage(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidDataException(
            "Google Drive returned a web page instead of the file. This usually means the "
                + "link's daily download quota is exhausted (try again in 24 hours), or the file "
                + "is no longer shared with 'anyone with the link'. You can also download the zip "
                + "manually in a browser and point the installer at it with the 'zip already on "
                + "disk' source."
        );
    }

    internal static string Human(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GiB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F1} MiB"
        : $"{bytes} B";
}
