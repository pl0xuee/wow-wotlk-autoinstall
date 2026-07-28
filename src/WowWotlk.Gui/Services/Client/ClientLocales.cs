using System.Text.RegularExpressions;

namespace WowWotlk.Gui.Services.Client;

/// <summary>
/// Which language build a client is. It is not recorded anywhere in the client — it is implied
/// by the one folder under Data named for a locale, and everything locale-specific (realmlist,
/// MPQ patches) has to go in that folder to be read at all.
/// </summary>
public static partial class ClientLocales
{
    /// <summary>The locale used when a client has none to read, and the overwhelmingly common one.</summary>
    public const string Fallback = "enUS";

    /// <summary>Locale folders present under Data, in name order.</summary>
    public static IReadOnlyList<string> Present(string clientRoot)
    {
        var data = Path.Join(clientRoot, "Data");
        try
        {
            return
            [
                .. Directory
                    .GetDirectories(data)
                    .Select(d => Path.GetFileName(d))
                    .Where(n => LocaleRx().IsMatch(n))
                    .OrderBy(n => n, StringComparer.Ordinal),
            ];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The locale a patch should be installed for.
    ///
    /// A client normally has exactly one. Where there are several — a repack that shipped two
    /// language folders — English wins, because that is what the patch and the server almost
    /// certainly assume; otherwise the first by name, so the answer is at least stable.
    /// </summary>
    public static string Detect(string clientRoot)
    {
        var present = Present(clientRoot);
        return present.FirstOrDefault(l => l.Equals(Fallback, StringComparison.OrdinalIgnoreCase))
            ?? present.FirstOrDefault(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? present.FirstOrDefault()
            ?? Fallback;
    }

    /// <summary>WoW locale folder names: two lowercase letters then two uppercase (enUS, deDE, zhTW).</summary>
    [GeneratedRegex("^[a-z]{2}[A-Z]{2}$")]
    private static partial Regex LocaleRx();
}
