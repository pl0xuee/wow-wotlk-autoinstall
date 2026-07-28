using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WowWotlk.Gui.Services.Addons;

/// <summary>A concrete zip to download, and the version string to record against it.</summary>
public sealed record ResolvedAddon(string DownloadUrl, string? Version);

/// <summary>
/// Turns a catalog <see cref="AddonSource"/> into a URL the installer can fetch. Kept apart
/// from the installer because this is the only part that talks to a third-party API, and it
/// is the part that breaks when a project moves or stops cutting releases.
/// </summary>
public class AddonResolver(IHttpClientFactory hcf, LogService log)
{
    public async Task<ResolvedAddon> ResolveAsync(AddonSource source, CancellationToken ct)
    {
        if (source.Kind == AddonSourceKind.Url)
        {
            return new ResolvedAddon(
                source.Url
                    ?? throw new InvalidOperationException(
                        "Catalog entry is a 'url' source but carries no url."
                    ),
                null
            );
        }
        var repo =
            source.Repo
            ?? throw new InvalidOperationException(
                "Catalog entry is a 'github' source but carries no owner/repo."
            );
        return await ResolveGithubAsync(repo, ct);
    }

    private async Task<ResolvedAddon> ResolveGithubAsync(string repo, CancellationToken ct)
    {
        var client = hcf.CreateClient();
        // api.github.com answers 403 to any request without a User-Agent. The factory sets one
        // for every client in the app, but a silent 403 here would look like a dead addon
        // rather than a misconfigured HTTP stack, so it is worth not depending on that.
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
            $"https://api.github.com/repos/{repo}/releases/latest",
            ct
        );
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Most of the good 3.3.5a addons are maintained as a plain branch and have never
            // tagged a release. GitHub still serves a zip of the default branch, and that zip
            // is the same thing users are told to download from the web UI.
            log.Append($"{repo} has no published releases; taking a zip of its default branch.");
            return new ResolvedAddon($"https://api.github.com/repos/{repo}/zipball", null);
        }
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var release = doc.RootElement;
        var version = release.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;

        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("browser_download_url", out var download)
                    && download.GetString() is { Length: > 0 } assetUrl
                )
                {
                    return new ResolvedAddon(assetUrl, version);
                }
            }
        }

        // A release with no packaged zip still has a source archive of the tag, which for an
        // addon repo is a working addon inside a wrapper folder AddonArchive knows to see past.
        if (
            release.TryGetProperty("zipball_url", out var zipball)
            && zipball.GetString() is { Length: > 0 } sourceZip
        )
        {
            return new ResolvedAddon(sourceZip, version);
        }

        throw new InvalidOperationException(
            $"The latest release of {repo} has no .zip asset and no source archive to fall back on."
        );
    }
}
