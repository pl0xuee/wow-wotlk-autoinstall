using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using WowWotlk.Gui.Services.Client;

namespace WowWotlk.Gui.Services.Patches;

/// <summary>
/// Installs MPQ patches into a client's Data/&lt;locale&gt; folder.
///
/// The game engine loads every patch-*.MPQ it finds there at startup, so installing one is
/// putting a file in a directory — there is no manifest to update and nothing to enable. What
/// makes it worth a service rather than a download is the two ways it goes wrong: the file has
/// to match the client's language or the engine ignores it, and its name has to not collide
/// with a patch the repack already shipped, because the engine would then load one and not the
/// other with no complaint.
/// </summary>
public class ClientPatchService(IHttpClientFactory hcf, InstalledPatchStore store, LogService log)
{
    public async Task<InstalledPatch> InstallAsync(
        string clientRoot,
        ClientPatch patch,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        var locale = ClientLocales.Detect(clientRoot);
        var assetName = patch.AssetFor(locale);
        var (url, version) = await ResolveAsync(patch, assetName, ct);

        var target = Path.Join(clientRoot, "Data", locale, assetName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        BackUpExisting(target);

        // Downloaded beside the target and renamed, so an interrupted download never leaves a
        // half-written MPQ where the engine will try to read it.
        var staging = target + ".part";
        try
        {
            await DownloadAsync(url, staging, progress, ct);
            File.Move(staging, target, overwrite: true);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }

        var record = new InstalledPatch(
            patch.Id,
            patch.Name,
            locale,
            Path.Join("Data", locale, assetName),
            version,
            DateTimeOffset.Now
        );
        store.Record(record);
        await store.SaveAsync();
        log.Append(
            $"Installed {patch.Name}{(version is null ? "" : " " + version)} → Data/{locale}/{assetName}"
        );
        return record;
    }

    public async Task RemoveAsync(string clientRoot, InstalledPatch patch)
    {
        var target = Path.Join(clientRoot, patch.RelativePath);
        TryDelete(target);
        // Put back whatever this patch displaced, so removing it leaves the client as it was.
        var backup = target + BackupSuffix;
        if (File.Exists(backup))
        {
            try
            {
                File.Move(backup, target, overwrite: true);
                log.Append($"Restored the patch that {patch.Name} had replaced.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Append($"Could not restore {backup} ({e.Message}); rename it back by hand.");
            }
        }
        store.Forget(patch.Id);
        await store.SaveAsync();
        log.Append($"Removed {patch.Name}.");
    }

    /// <summary>Whether this patch's file is actually present in the client right now.</summary>
    public static bool IsPresent(string clientRoot, InstalledPatch patch) =>
        File.Exists(Path.Join(clientRoot, patch.RelativePath));

    /// <summary>The release asset's download URL and the tag it came from.</summary>
    private async Task<(string Url, string? Version)> ResolveAsync(
        ClientPatch patch,
        string assetName,
        CancellationToken ct
    )
    {
        var client = Client();
        // "latest" is the newest non-prerelease, which is what the stable patch is published as;
        // the beta line is marked pre-release and is deliberately not what this picks up.
        var json = await client.GetStringAsync(
            $"https://api.github.com/repos/{patch.Repo}/releases/latest",
            ct
        );
        using var doc = JsonDocument.Parse(json);
        var version = doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;

        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() is { } name
                    && name.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("browser_download_url", out var url)
                    && url.GetString() is { Length: > 0 } download)
                {
                    return (download, version);
                }
            }
        }
        throw new InvalidOperationException(
            $"{patch.Repo} release {version ?? "latest"} has no {assetName}. The patch may not "
                + "cover this client's language."
        );
    }

    private async Task DownloadAsync(
        string url,
        string path,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        using var response = await Client().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);
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

    /// <summary>
    /// Moves aside a file already using this patch's name.
    ///
    /// Repacks ship their own patch-&lt;locale&gt;-X.MPQ files, and two patches cannot share a
    /// letter — the engine loads one and silently ignores the other. Overwriting would remove
    /// content the client was built around, with the symptom appearing much later as missing
    /// models or textures.
    /// </summary>
    private void BackUpExisting(string target)
    {
        if (!File.Exists(target))
        {
            return;
        }
        var backup = target + BackupSuffix;
        try
        {
            File.Move(target, backup, overwrite: true);
            log.Append(
                $"{Path.GetFileName(target)} was already there and has been kept as "
                    + $"{Path.GetFileName(backup)}. If the client loses content, put it back."
            );
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Append($"Could not set aside the existing {Path.GetFileName(target)}: {e.Message}");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Append($"Could not delete {path} ({e.Message}); remove it by hand.");
        }
    }

    private HttpClient Client()
    {
        var client = hcf.CreateClient();
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(AppUpdateService.RepoName, AppUpdateService.CurrentVersion)
            );
        }
        return client;
    }

    internal const string BackupSuffix = ".replaced-by-wotlk-autoinstall";
}
