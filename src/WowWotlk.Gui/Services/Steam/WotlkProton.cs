using System.Text.RegularExpressions;

namespace WowWotlk.Gui.Services.Steam;

public enum ProtonSuitability
{
    /// <summary>GE-Proton. Ships the media codecs and DXVK tweaks the 3.3.5a client is happiest on.</summary>
    Preferred,

    /// <summary>Valve Proton (numbered or Experimental). Runs 3.3.5a fine; the default fallback.</summary>
    Supported,

    /// <summary>Anything else in compatibilitytools.d — a fork we have no opinion about.</summary>
    Unknown,
}

/// <summary>
/// Ranks Proton builds for a vanilla WotLK client.
///
/// Unlike a Wabbajack list, 3.3.5a pins nothing: it is a 2010 D3D9 game and every current
/// Proton runs it. So this ranks rather than requires — GE first for its codec/DXVK
/// packaging, then Valve Proton, newest build within each tier — and the only build it will
/// actually refuse is one whose Steam Linux Runtime is not installed, which cannot launch at
/// all.
/// </summary>
public static partial class WotlkProton
{
    public const string Guidance =
        "Any modern Proton runs a 3.3.5a client. GE-Proton is preferred; install one with "
        + "ProtonUp-Qt if the list below is empty.";

    public static ProtonSuitability Evaluate(CompatTool tool)
    {
        var name = tool.InternalName;
        if (name.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase))
        {
            return ProtonSuitability.Preferred;
        }
        // Valve's own builds are named "proton_9", "proton_experimental", "Proton 9.0" …
        if (name.StartsWith("proton", StringComparison.OrdinalIgnoreCase)
            || tool.DisplayName.StartsWith("Proton", StringComparison.OrdinalIgnoreCase))
        {
            return ProtonSuitability.Supported;
        }
        return ProtonSuitability.Unknown;
    }

    /// <summary>Ranking key for the Proton list: best choice first.</summary>
    public static int Rank(CompatTool tool) => (int)Evaluate(tool);

    /// <summary>
    /// Best tier first, and within one tier the newest build first, so a fallback lands on the
    /// newest usable build rather than an arbitrary one.
    /// </summary>
    public static IEnumerable<CompatTool> Order(IEnumerable<CompatTool> tools) =>
        tools
            .OrderBy(Rank)
            .ThenByDescending(
                t => VersionKey(t.DisplayName + " " + t.InternalName),
                VersionComparer.Instance
            );

    // long, and clamp on overflow: a date-stamped build (GE-Proton-20250101120000) exceeds
    // int range and would otherwise throw OverflowException, crashing the Steam page.
    private static long[] VersionKey(string s) =>
        NumberRx()
            .Matches(s)
            .Select(m => long.TryParse(m.Value, out var n) ? n : long.MaxValue)
            .ToArray();

    private sealed class VersionComparer : IComparer<long[]>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(long[]? x, long[]? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var xi = i < x.Length ? x[i] : 0;
                var yi = i < y.Length ? y[i] : 0;
                if (xi != yi)
                {
                    return xi.CompareTo(yi);
                }
            }
            return 0;
        }
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRx();

    public static string Describe(ProtonSuitability suitability) =>
        suitability switch
        {
            ProtonSuitability.Preferred => "GE-Proton, preferred for 3.3.5a",
            ProtonSuitability.Supported => "Valve Proton, runs 3.3.5a fine",
            _ => "unrecognised build, untested with 3.3.5a",
        };

    /// <summary>Outcome of picking a Proton build for this machine.</summary>
    public sealed record ProtonSelection(
        CompatTool? Tool,
        ProtonSuitability Suitability,
        CompatTool? SubstitutedFor
    );

    /// <summary>
    /// Picks the Proton build to install with. A build whose Steam Linux Runtime is absent
    /// cannot run at all, so usability is filtered before ranking rather than discovered when
    /// the user clicks Play and gets nothing.
    ///
    /// A pin is honoured whenever it can run — with no hard compatibility rule to enforce,
    /// there is no reason to override a deliberate choice.
    /// </summary>
    public static ProtonSelection Select(
        IEnumerable<CompatTool> tools,
        Func<int, bool> runtimeInstalled,
        string? pinned
    )
    {
        var ranked = Order(tools).ToList();
        var wanted =
            string.IsNullOrWhiteSpace(pinned)
                ? ranked.FirstOrDefault()
                : ranked.FirstOrDefault(t =>
                    t.InternalName.Equals(pinned, StringComparison.OrdinalIgnoreCase)
                );

        var chosen = wanted is not null && IsUsable(wanted, runtimeInstalled)
            ? wanted
            : ranked.FirstOrDefault(t => IsUsable(t, runtimeInstalled));

        if (chosen is null)
        {
            return new ProtonSelection(null, ProtonSuitability.Unknown, wanted);
        }
        return new ProtonSelection(
            chosen,
            Evaluate(chosen),
            ReferenceEquals(chosen, wanted) ? null : wanted
        );
    }

    /// <summary>Why a build was passed over, in words that say what to do about it.</summary>
    public static string SubstitutionReason(CompatTool replaced, Func<int, bool> runtimeInstalled) =>
        replaced.RequiredRuntimeAppId is { } appId && !runtimeInstalled(appId)
            ? $"needs {SteamRuntimeCatalog.Describe(appId)}, which is not installed"
            : "is not usable on this machine";

    private static bool IsUsable(CompatTool tool, Func<int, bool> runtimeInstalled) =>
        tool.RequiredRuntimeAppId is not { } appId || runtimeInstalled(appId);
}
