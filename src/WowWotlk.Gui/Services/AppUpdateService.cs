using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace WowWotlk.Gui.Services;

public sealed record AppUpdateCheck(
    bool UpdateAvailable,
    string CurrentVersion,
    string LatestTag,
    string ReleaseUrl,
    string? AssetUrl,
    string? AssetSha256
);

/// <summary>Checks this app's own GitHub Releases for a newer version and self-updates.</summary>
public class AppUpdateService(IHttpClientFactory hcf)
{
    public const string RepoOwner = "pl0xuee";
    public const string RepoName = "wow-wotlk-autoinstall";

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    /// <summary>The AppImage file we were launched from; null when not running as an AppImage.</summary>
    public static string? InstalledAppImagePath => Environment.GetEnvironmentVariable("APPIMAGE");

    /// <summary>
    /// Environment an AppImage sets for the process it runs, all of it describing the image
    /// currently mounted. The replacement mounts itself and sets its own, so these have to be
    /// cleared — inherited, they point the new instance at the old mount, which is unmounted
    /// the moment this process exits.
    /// </summary>
    private static readonly string[] AppImageEnvironment =
        ["APPIMAGE", "APPDIR", "ARGV0", "OWD", "LD_LIBRARY_PATH", "LD_PRELOAD", "PYTHONHOME"];

    /// <summary>
    /// Starts the AppImage at <paramref name="path"/> as an independent process and returns
    /// whether it started.
    ///
    /// Detached with setsid so it survives this process exiting — without a new session it is
    /// a child of the dying app and dies with it. The caller shuts down only on true: exiting
    /// after a failed spawn would leave the user with no window and no obvious way back.
    /// </summary>
    public bool TryRelaunch(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            // exec so the shell does not linger; "$0" keeps a path with spaces intact.
            psi.ArgumentList.Add("exec setsid \"$0\" >/dev/null 2>&1 &");
            psi.ArgumentList.Add(path);
            foreach (var name in AppImageEnvironment)
            {
                psi.Environment.Remove(name);
            }
            using var started = System.Diagnostics.Process.Start(psi);
            return started is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<AppUpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var json = await _httpClient.GetStringAsync(
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest",
            ct
        );
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var url = doc.RootElement.GetProperty("html_url").GetString() ?? "";
        string? assetUrl = null;
        string? assetSha256 = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    // GitHub exposes a per-asset content digest ("sha256:<hex>"); use it to
                    // verify the download before executing it.
                    if (asset.TryGetProperty("digest", out var digest)
                        && digest.GetString() is { } d
                        && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        assetSha256 = d["sha256:".Length..];
                    }
                    break;
                }
            }
        }
        var hasUpdate = IsNewer(tag, CurrentVersion);
        return new AppUpdateCheck(hasUpdate, CurrentVersion, tag, url, assetUrl, assetSha256);
    }

    /// <summary>
    /// Whether <paramref name="tag"/> names a release newer than <paramref name="current"/>.
    ///
    /// Normalised to three components before comparing, because Version treats a missing
    /// component as "less than zero" rather than zero — so tag "v0.2" would read as older than
    /// "0.2.0" and an update would be silently withheld. A pre-release or build suffix is
    /// dropped rather than failing the parse, which would report "you are up to date" when the
    /// truth is that the tag could not be read.
    /// </summary>
    internal static bool IsNewer(string tag, string current)
    {
        return Normalise(tag) is { } latestVersion
            && Normalise(current) is { } currentVersion
            && latestVersion > currentVersion;

        static Version? Normalise(string value)
        {
            var text = value.Trim().TrimStart('v', 'V');
            var cut = text.IndexOfAny(['-', '+']);
            if (cut >= 0)
            {
                text = text[..cut];
            }
            if (!Version.TryParse(text, out var parsed))
            {
                return null;
            }
            return new Version(
                parsed.Major,
                parsed.Minor,
                parsed.Build < 0 ? 0 : parsed.Build
            );
        }
    }

    /// <summary>
    /// Downloads the release AppImage next to the installed one and atomically swaps it in.
    /// The running instance keeps its mounted (old) image; caller relaunches and exits.
    /// Returns the path to the updated AppImage.
    /// </summary>
    public async Task<string> DownloadAndInstallAsync(
        string assetUrl,
        string? expectedSha256,
        IProgress<double>? progress = null,
        CancellationToken ct = default
    )
    {
        var target =
            InstalledAppImagePath
            ?? throw new InvalidOperationException("Not running from an AppImage");
        // Same directory as the target so the final rename is atomic (same filesystem).
        var staging = target + ".update-new";
        try
        {
            byte[] hash;
            using (var response = await _httpClient.GetAsync(
                assetUrl,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            ))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var dest = File.Create(staging);
                using var sha = System.Security.Cryptography.SHA256.Create();
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    if (total > 0)
                    {
                        progress?.Report((double)done / total.Value);
                    }
                }
                sha.TransformFinalBlock([], 0, 0);
                hash = sha.Hash!;
            }

            // Never execute an unverified binary. GitHub always supplies the digest; if it's
            // ever absent, refuse rather than swap in something we can't check.
            var actual = Convert.ToHexStringLower(hash);
            if (string.IsNullOrEmpty(expectedSha256))
            {
                throw new InvalidOperationException(
                    "Release asset has no published checksum; refusing to install unverified update"
                );
            }
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Downloaded update failed checksum verification (expected {expectedSha256}, got {actual})"
                );
            }
            // An AppImage is a Linux-only artifact, but the guard keeps the call site honest
            // for the analyzer rather than suppressing it.
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    staging,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                );
            }
            File.Move(staging, target, overwrite: true);
            return target;
        }
        catch
        {
            try
            {
                File.Delete(staging);
            }
            catch
            {
                // best-effort cleanup of the partial download
            }
            throw;
        }
    }

    private readonly HttpClient _httpClient = hcf.CreateClient();
}
