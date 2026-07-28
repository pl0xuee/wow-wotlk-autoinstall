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

        // Names that other entries live under, so a zero-length entry with no trailing slash
        // can be recognised as the directory it really is.
        var entriesUnderPrefix = archive
            .Entries.Select(e => e.FullName.Replace('\\', '/'))
            .Where(n => n.Contains('/'))
            .Select(n => n[..n.LastIndexOf('/')])
            .SelectMany(AncestorsOf)
            .ToHashSet(StringComparer.Ordinal);

        long written = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            // Some Windows zip tools store paths with backslashes. Left alone those become one
            // literal filename containing slashes, so the client extracts to a flat pile of
            // oddly-named files that looks like a success and cannot run.
            var entryPath = entry.FullName.Replace('\\', '/');
            var target = Path.GetFullPath(Path.Combine(destinationDir, entryPath));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                // Zip slip: an entry named ../../.bashrc would otherwise write outside the
                // folder the user chose.
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' escapes the destination folder; refusing to extract it."
                );
            }

            // A directory is normally stored with a trailing slash, which leaves Name empty.
            // Some writers omit the slash and store it as a zero-length entry instead; taken
            // as a file that creates "Data" as a regular file, and the next entry under it
            // then fails to create its directory — tens of minutes into a 16 GiB archive, and
            // identically on every retry, because File.Create just truncates it again.
            var isDirectory =
                entry.Name.Length == 0
                || (entry.Length == 0 && entriesUnderPrefix.Contains(entryPath));
            if (isDirectory)
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

    /// <summary>"Data/enUS/Interface" → Data/enUS/Interface, Data/enUS, Data.</summary>
    private static IEnumerable<string> AncestorsOf(string dir)
    {
        var current = dir;
        while (current.Length > 0)
        {
            yield return current;
            var cut = current.LastIndexOf('/');
            current = cut < 0 ? "" : current[..cut];
        }
    }
}
