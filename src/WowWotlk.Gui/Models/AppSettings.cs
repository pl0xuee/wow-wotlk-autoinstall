using System.Text.Json;
using System.Text.Json.Serialization;

namespace WowWotlk.Gui.Models;

/// <summary>Where the client comes from. Decided on the Install page, remembered here.</summary>
public enum ClientSource
{
    /// <summary>Download the 3.3.5a zip from Google Drive.</summary>
    GoogleDrive,

    /// <summary>Use a zip already sitting on disk.</summary>
    LocalZip,

    /// <summary>Use an already-extracted client folder; nothing is downloaded or unpacked.</summary>
    ExistingFolder,
}

public class AppSettings
{
    public string InstallDir { get; set; } = DefaultInstallDir;
    public string DownloadDir { get; set; } = DefaultDownloadDir;

    /// <summary>
    /// Realmlist the client is pointed at. Use 127.0.0.1 for an AzerothCore/TrinityCore box on
    /// this machine, or the LAN address of the one running it.
    /// </summary>
    public string ServerAddress { get; set; } = DefaultServerAddress;

    public ClientSource ClientSource { get; set; } = ClientSource.GoogleDrive;

    /// <summary>Zip already on disk, used when <see cref="ClientSource"/> is LocalZip.</summary>
    public string? LocalZipPath { get; set; }

    /// <summary>Extracted client folder, used when <see cref="ClientSource"/> is ExistingFolder.</summary>
    public string? ExistingClientPath { get; set; }

    /// <summary>
    /// Google Drive file id of the client zip. Replaced on the Settings page to install from a
    /// different upload — or when this one is re-uploaded, which mints a new id.
    /// </summary>
    public string DriveFileId { get; set; } = DefaultDriveFileId;

    /// <summary>
    /// Exact byte length the downloaded zip must have. Drive serves an HTML error page with
    /// HTTP 200 when the daily quota is hit, so size is the check that catches a truncated or
    /// substituted download before an hour is spent unpacking it. Zero disables the check.
    /// </summary>
    public long ExpectedZipBytes { get; set; } = DefaultExpectedZipBytes;

    /// <summary>Internal name of the preferred compat tool (e.g. GE-Proton10-4); null = auto-pick.</summary>
    public string? PreferredProtonInternalName { get; set; }

    public bool SetupSteamAfterInstall { get; set; } = true;

    /// <summary>
    /// Resolution to write into the client as "WxH". Null means "leave the client alone", so
    /// an install on a machine whose displays could not be read changes nothing.
    /// </summary>
    public string? PreferredResolution { get; set; }

    /// <summary>Run the client in a window rather than fullscreen.</summary>
    public bool Windowed { get; set; }

    /// <summary>Whether the one-click install also installs client patches.</summary>
    public bool InstallPatchesAfterInstall { get; set; } = true;

    /// <summary>
    /// Patch ids the one-click install will fetch. Null means "the recommended set"; an empty
    /// list is a real choice to install none.
    /// </summary>
    public List<string>? SelectedPatchIds { get; set; }

    /// <summary>Whether the one-click install also installs addons.</summary>
    public bool InstallAddonsAfterInstall { get; set; } = true;

    /// <summary>
    /// Catalog ids the one-click install will fetch. Null means "the recommended set", so a
    /// fresh install follows the catalog rather than a list frozen at first run; an empty list
    /// is a real choice to install none and is kept as such.
    /// </summary>
    public List<string>? SelectedAddonIds { get; set; }

    /// <summary>
    /// Resolved client root (the folder holding Wow.exe) from the last successful install.
    /// The Addons and Steam pages both need it and neither should have to re-scan for it.
    /// </summary>
    public string? ClientRoot { get; set; }

    /// <summary>
    /// The realm a fresh install points at, so the one-click path works with nothing typed in.
    ///
    /// This is a real address in a public repository, which is the deliberate price of that: the
    /// server it names is reachable by anyone who reads this file or runs a build of the app.
    /// </summary>
    public const string DefaultServerAddress = "209.25.140.23:1170";

    /// <summary>
    /// The client zip a fresh install downloads, for the same reason the realm is set above —
    /// nothing to paste in before the first run.
    ///
    /// It is one public share link shipped to every copy of the app, and Google's download quota
    /// is per file, so that daily allowance is shared between all of them rather than one each.
    /// When it runs out the downloader says so (Drive answers with an HTML page and a 200) and
    /// the way through is the Zip-on-disk source.
    /// </summary>
    public const string DefaultDriveFileId = "171vDSVws4R50xc6JGpoputrkHMAdZ7Kx";

    /// <summary>
    /// Size of a stock 3.3.5a client zip, as a starting point. Wrong for any other upload, so
    /// the Settings page lets it be corrected — and 0 turns the check off entirely.
    /// </summary>
    public const long DefaultExpectedZipBytes = 17_675_893_897L;

    public async Task SaveAsync()
    {
        if (!Directory.Exists(AppDataPath))
        {
            Directory.CreateDirectory(AppDataPath);
        }
        await Services.AtomicFile.WriteAllTextAsync(
            SettingsPath,
            JsonSerializer.Serialize(this, AppSettingsCtx.Default.AppSettings)
        );
    }

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }
        try
        {
            return JsonSerializer.Deserialize(
                    File.ReadAllText(SettingsPath),
                    AppSettingsCtx.Default.AppSettings
                ) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // A corrupt settings.json must not brick the app; keep the broken file for
            // inspection and start fresh in memory (only persisted if the user saves).
            File.Copy(SettingsPath, SettingsPath + ".corrupt", true);
            return new AppSettings();
        }
    }

    public static readonly string AppDataPath = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "wow-wotlk-autoinstall"
    );

    public static string SettingsPath => Path.Join(AppDataPath, "settings.json");

    /// <summary>Expands a leading ~/ so a hand-edited settings file works like a shell path.</summary>
    public static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    private static string DefaultInstallDir =>
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "WoW-WotLK");

    private static string DefaultDownloadDir =>
        Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Games",
            "WoW-WotLK-downloads"
        );
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, Converters = [typeof(JsonStringEnumConverter<ClientSource>)])]
public partial class AppSettingsCtx : JsonSerializerContext;
