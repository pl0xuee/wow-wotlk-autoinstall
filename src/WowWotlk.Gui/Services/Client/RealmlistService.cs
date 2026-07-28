using System.Text.RegularExpressions;

namespace WowWotlk.Gui.Services.Client;

/// <summary>
/// Points the client at a server by writing <c>set realmlist &lt;address&gt;</c> into every
/// realmlist.wtf the install has.
///
/// There is more than one. 3.3.5a reads the file under the locale folder it was built for
/// (Data/enUS, Data/enGB, …) and many repacks ship a second copy at the client root, so
/// editing only one leaves the client dialling whatever the repack author's server was. All of
/// them are written, and every other line in the file is preserved — some builds keep
/// <c>set patchlist</c> next to it.
/// </summary>
public partial class RealmlistService(LogService log)
{
    /// <summary>Applies the address and returns the files that were written.</summary>
    public IReadOnlyList<string> Apply(string clientRoot, string serverAddress)
    {
        var address = Normalise(serverAddress);
        var written = new List<string>();
        foreach (var file in TargetFiles(clientRoot))
        {
            var existing = File.Exists(file) ? File.ReadAllText(file) : "";
            var updated = SetRealmlist(existing, address);
            if (existing == updated && File.Exists(file))
            {
                written.Add(file);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.WriteAllText(file, updated);
            written.Add(file);
        }
        log.Append($"realmlist set to '{address}' in {written.Count} file(s).");
        ClearCache(clientRoot);
        return written;
    }

    /// <summary>The address currently configured, read from the first realmlist.wtf that has one.</summary>
    public string? Read(string clientRoot)
    {
        foreach (var file in TargetFiles(clientRoot).Where(File.Exists))
        {
            var match = RealmlistLineRx().Match(File.ReadAllText(file));
            if (match.Success)
            {
                return match.Groups["address"].Value.Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Every realmlist.wtf this install should have. Locale folders that already hold one are
    /// used as-is; a client with none gets one written under whichever locale folder it has,
    /// falling back to Data/enUS.
    /// </summary>
    internal static IEnumerable<string> TargetFiles(string clientRoot)
    {
        var files = new List<string> { Path.Join(clientRoot, "realmlist.wtf") };
        var dataDir = Path.Join(clientRoot, "Data");
        var localeDirs = Directory.Exists(dataDir)
            ? Directory.GetDirectories(dataDir).Where(d => LocaleRx().IsMatch(Path.GetFileName(d))).ToList()
            : [];
        if (localeDirs.Count == 0)
        {
            localeDirs.Add(Path.Join(dataDir, "enUS"));
        }
        files.AddRange(localeDirs.Select(d => Path.Join(d, "realmlist.wtf")));
        // A root realmlist.wtf is only written when the client already had one; a plain 3.3.5a
        // install keeps it under the locale folder and inventing one at the root is noise.
        return files.Where((f, i) => i != 0 || File.Exists(f));
    }

    /// <summary>
    /// Replaces the <c>set realmlist</c> line, preserving every other line. Exposed for tests:
    /// the failure this guards against is silently dropping a <c>set patchlist</c> line that
    /// the client needs.
    /// </summary>
    internal static string SetRealmlist(string existing, string address)
    {
        var line = $"set realmlist {address}";
        if (RealmlistLineRx().IsMatch(existing))
        {
            // Every occurrence, not just the first. The client applies set directives in file
            // order, so the last one wins — replacing only the first leaves a stale address
            // still in effect while this method reports having changed it.
            //
            // A MatchEvaluator rather than a replacement string: $ is a substitution character
            // there, so an address containing one would be expanded against the capture group.
            return RealmlistLineRx().Replace(existing, _ => line);
        }
        return existing.Length == 0 ? line + "\n"
            : existing.EndsWith('\n') ? existing + line + "\n"
            : existing + "\n" + line + "\n";
    }

    /// <summary>
    /// Strips a scheme, port or stray quoting a user may have pasted in. The client wants a
    /// bare host or IP; anything else silently fails to connect with no message.
    /// </summary>
    internal static string Normalise(string serverAddress)
    {
        var value = serverAddress.Trim().Trim('"');
        foreach (var scheme in (string[])["http://", "https://", "realmlist://"])
        {
            if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                value = value[scheme.Length..];
            }
        }
        // Trailing path and port are not part of a realmlist entry. IPv6 is not supported by
        // a 2010 client, so a lone colon is always a port.
        var cut = value.IndexOfAny([':', '/']);
        if (cut >= 0)
        {
            value = value[..cut];
        }
        return value.Length == 0 ? "127.0.0.1" : value;
    }

    /// <summary>
    /// Deletes the client's Cache folder. It caches the realm list it last saw, so a client
    /// pointed at a new address keeps offering the old realm until this is cleared — the most
    /// common "I changed realmlist and nothing happened" report there is.
    /// </summary>
    private void ClearCache(string clientRoot)
    {
        var cache = Path.Join(clientRoot, "Cache");
        if (!Directory.Exists(cache))
        {
            return;
        }
        try
        {
            Directory.Delete(cache, recursive: true);
            log.Append("Cleared the client Cache folder so the new realm is picked up.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Append($"Could not clear the Cache folder ({e.Message}); delete it by hand if the old realm still shows.");
        }
    }

    [GeneratedRegex(@"^[ \t]*set[ \t]+realmlist[ \t]+(?<address>.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex RealmlistLineRx();

    /// <summary>WoW locale folder names: two lowercase letters then two uppercase (enUS, deDE, zhTW).</summary>
    [GeneratedRegex("^[a-z]{2}[A-Z]{2}$")]
    private static partial Regex LocaleRx();
}
