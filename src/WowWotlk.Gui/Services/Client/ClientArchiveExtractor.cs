using System.IO.Compression;

namespace WowWotlk.Gui.Services.Client;

public sealed record ExtractProgress(long BytesWritten, long TotalBytes, string CurrentEntry)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesWritten / TotalBytes, 0, 1) : 0;
}

/// <summary>
/// Unpacks the client zip. Progress is measured in uncompressed bytes rather than entry count
/// because the client's few enormous MPQ archives dominate the wall clock — a file counter
/// would sit at 3% for twenty minutes and then jump to done.
/// </summary>
public class ClientArchiveExtractor(LogService log)
{
    public async Task ExtractAsync(
        string zipPath,
        string destinationDir,
        IProgress<ExtractProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        Directory.CreateDirectory(destinationDir);
        // Compare resolved paths, and with a trailing separator so /games/wow-old is not
        // treated as inside /games/wow.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDir))
            + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        var total = archive.Entries.Sum(e => e.Length);
        log.Append(
            $"Extracting {Path.GetFileName(zipPath)} ({archive.Entries.Count} entries, "
                + $"{GoogleDriveDownloader.Human(total)}) to {destinationDir}"
        );

        long written = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                // Zip slip: an entry named ../../.bashrc would otherwise write outside the
                // folder the user chose.
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' escapes the destination folder; refusing to extract it."
                );
            }

            // Directory entries have an empty name and zero length.
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var source = entry.Open())
            await using (var dest = File.Create(target))
            {
                await source.CopyToAsync(dest, ct);
            }
            written += entry.Length;
            progress?.Report(new ExtractProgress(written, total, entry.FullName));
        }
        log.Append("Extraction finished.");
    }
}
