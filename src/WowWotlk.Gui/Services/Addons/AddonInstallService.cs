using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;

namespace WowWotlk.Gui.Services.Addons;

/// <summary>
/// Puts addons into a client's Interface/AddOns and takes them out again.
///
/// Catalog, URL and local-zip installs all funnel through one path: stage into a temp folder,
/// unpack there, work out which directories are addons, and only then touch the client. An
/// archive that turns out to hold no .toc therefore fails before anything under AddOns has
/// been deleted, rather than half-way through replacing a working addon.
/// </summary>
public class AddonInstallService(
    IHttpClientFactory hcf,
    AddonResolver resolver,
    InstalledAddonStore store,
    LogService log
)
{
    public string AddOnsDir(string clientRoot) => Path.Join(clientRoot, "Interface", "AddOns");

    public string DisabledDir(string clientRoot) =>
        Path.Join(clientRoot, "Interface", "AddOns.disabled");

    public async Task<InstalledAddon> InstallFromCatalogAsync(
        string clientRoot,
        CatalogAddon addon,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        var resolved = await resolver.ResolveAsync(addon.Source, ct);
        log.Append($"Installing {addon.Name} from {resolved.DownloadUrl}");
        return await InstallAsync(
            clientRoot,
            resolved.DownloadUrl,
            localZip: null,
            id: addon.Id,
            name: addon.Name,
            version: resolved.Version,
            source: $"catalog:{addon.Id}",
            progress,
            ct
        );
    }

    public Task<InstalledAddon> InstallFromUrlAsync(
        string clientRoot,
        string url,
        IProgress<double>? progress,
        CancellationToken ct
    ) =>
        InstallAsync(
            clientRoot,
            url,
            localZip: null,
            id: null,
            name: null,
            version: null,
            source: "url",
            progress,
            ct
        );

    public Task<InstalledAddon> InstallFromZipAsync(
        string clientRoot,
        string zipPath,
        CancellationToken ct
    ) =>
        InstallAsync(
            clientRoot,
            downloadUrl: null,
            localZip: zipPath,
            id: null,
            name: null,
            version: null,
            source: "zip",
            progress: null,
            ct
        );

    /// <summary>Deletes every folder the addon owns, from both the enabled and disabled trees, and forgets it.</summary>
    public async Task RemoveAsync(string clientRoot, InstalledAddon addon)
    {
        foreach (var folder in addon.Folders)
        {
            // A folder can legitimately be in either tree — the user may have disabled part of
            // a multi-folder addon — so both are cleared rather than whichever one looks live.
            foreach (
                var dir in (string[])
                    [
                        Path.Join(AddOnsDir(clientRoot), folder),
                        Path.Join(DisabledDir(clientRoot), folder),
                    ]
            )
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log.Append($"Could not delete {dir} ({e.Message}); remove it by hand.");
                }
            }
        }
        store.Forget(addon.Id);
        await store.SaveAsync();
        log.Append($"Removed {addon.Name}.");
    }

    /// <summary>
    /// Enables or disables one folder by moving it between Interface/AddOns and
    /// Interface/AddOns.disabled.
    ///
    /// The client's own switch is WTF/Account/&lt;ACCOUNT&gt;/AddOns.txt, which is not usable
    /// here: it does not exist until an account has logged in at least once, so on a fresh
    /// install there is no WTF/Account directory to edit and the toggle would silently do
    /// nothing. Once there are several accounts there is also no way to know which one the
    /// user will play. A folder that is not under AddOns is not loaded, on any account,
    /// always.
    /// </summary>
    public void SetEnabled(string clientRoot, string folderName, bool enabled)
    {
        var from = Path.Join(enabled ? DisabledDir(clientRoot) : AddOnsDir(clientRoot), folderName);
        var to = Path.Join(enabled ? AddOnsDir(clientRoot) : DisabledDir(clientRoot), folderName);
        if (!Directory.Exists(from))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(to))
        {
            // The same folder in both trees is the wreckage of an interrupted toggle. The one
            // being moved is the copy the user last acted on, so it is the one that survives.
            Directory.Delete(to, recursive: true);
        }
        MoveOrCopy(from, to);
        log.Append($"{folderName} {(enabled ? "enabled" : "disabled")}.");
    }

    private async Task<InstalledAddon> InstallAsync(
        string clientRoot,
        string? downloadUrl,
        string? localZip,
        string? id,
        string? name,
        string? version,
        string source,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        var staging = Directory.CreateTempSubdirectory("wow-addon-");
        try
        {
            var zip = localZip ?? await DownloadAsync(downloadUrl!, staging.FullName, progress, ct);
            var unpacked = Path.Join(staging.FullName, "unpacked");
            await ExtractAsync(zip, unpacked, ct);

            var folders = AddonArchive.FindAddonFolders(unpacked);
            if (folders.Count == 0)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(zip)} contains no .toc file, so it is not a World of "
                        + "Warcraft addon. Addon zips hold one folder per addon, each with a "
                        + "matching .toc inside."
                );
            }

            var installed = new List<string>();
            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                var folderName = Path.GetFileName(folder);
                var destination = Path.Join(AddOnsDir(clientRoot), folderName);
                if (Directory.Exists(destination))
                {
                    // A partial overwrite leaves files from the old version behind, which for
                    // an addon that renamed a Lua file means both get loaded.
                    Directory.Delete(destination, recursive: true);
                }
                MoveOrCopy(folder, destination);
                installed.Add(folderName);
            }

            var (tocTitle, tocVersion, _) = ReadToc(
                Path.Join(AddOnsDir(clientRoot), installed[0])
            );
            var record = new InstalledAddon(
                // A manual install has no catalog id, so the first folder name stands in: it is
                // stable across re-installs of the same addon, which is what Record() needs to
                // replace rather than duplicate the entry.
                id ?? installed[0],
                name ?? tocTitle ?? installed[0],
                installed,
                tocVersion ?? version,
                source,
                DateTimeOffset.Now
            );
            store.Record(record);
            await store.SaveAsync();
            log.Append(
                $"Installed {record.Name}{(record.Version is null ? "" : " " + record.Version)} "
                    + $"({installed.Count} folder(s): {string.Join(", ", installed)})."
            );
            return record;
        }
        finally
        {
            try
            {
                staging.Delete(recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Append($"Could not clean up {staging.FullName} ({e.Message}).");
            }
        }
    }

    private async Task<string> DownloadAsync(
        string url,
        string intoDir,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        var client = hcf.CreateClient();
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(
                    AppUpdateService.RepoName,
                    AppUpdateService.CurrentVersion
                )
            );
        }

        using var response = await client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        response.EnsureSuccessStatusCode();

        var path = Path.Join(intoDir, "addon.zip");
        // GitHub serves source zipballs chunked with no Content-Length. Addon downloads are a
        // few megabytes, so an indeterminate bar for those is better than inventing a total.
        var total = response.Content.Headers.ContentLength ?? 0;
        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(path))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                {
                    progress?.Report(Math.Clamp((double)done / total, 0, 1));
                }
            }
        }
        return path;
    }

    private static async Task ExtractAsync(string zipPath, string destinationDir, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDir);
        // Compare resolved paths, and with a trailing separator so /tmp/x-old is not treated
        // as inside /tmp/x.
        var root =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDir))
            + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                // Zip slip: an entry named ../../.bashrc would otherwise write outside the
                // staging folder — and an addon zip is a file a stranger uploaded.
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
            await using var source = entry.Open();
            await using var dest = File.Create(target);
            await source.CopyToAsync(dest, ct);
        }
    }

    private static (string? Title, string? Version, string? Interface) ReadToc(string addonDir)
    {
        if (AddonArchive.TocIn(addonDir) is not { } toc)
        {
            return (null, null, null);
        }
        try
        {
            return AddonArchive.ParseToc(File.ReadAllText(toc));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// Renames <paramref name="sourceDir"/> onto <paramref name="destinationDir"/>, falling
    /// back to a recursive copy.
    ///
    /// Directory.Move is a rename, which the kernel refuses across mounts with EXDEV and .NET
    /// surfaces as IOException. The staging folder lives under /tmp, which on most Linux
    /// desktops is a tmpfs and so always a different mount from the game directory — the copy
    /// is the normal path here, not the exceptional one.
    /// </summary>
    private static void MoveOrCopy(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationDir)!);
        try
        {
            Directory.Move(sourceDir, destinationDir);
            return;
        }
        catch (IOException)
        {
            // Different filesystem; copy instead.
        }
        CopyTree(sourceDir, destinationDir);
        Directory.Delete(sourceDir, recursive: true);
    }

    private static void CopyTree(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Join(destinationDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyTree(dir, Path.Join(destinationDir, Path.GetFileName(dir)));
        }
    }
}
