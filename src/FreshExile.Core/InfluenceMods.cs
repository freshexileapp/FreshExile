using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>
/// Whose influence a mod line is, by its text: the eldritch implicits of the Exarch and the Eater, and the explicits only
/// the Shaper, the Elder and the conquerors give. From Data/InfluenceMods.tsv, made by tools/update-influence-mods.ps1 out of RePoE.
/// </summary>
public static partial class InfluenceMods
{
    static readonly Lazy<(Dictionary<string, Influence[]> Implicit, Dictionary<string, Influence[]> Explicit)> Data = new(Load);

    static (Dictionary<string, Influence[]>, Dictionary<string, Influence[]>) Load()
    {
        var imp = new Dictionary<string, Influence[]>(StringComparer.Ordinal);
        var exp = new Dictionary<string, Influence[]>(StringComparer.Ordinal);
        using var s = typeof(InfluenceMods).Assembly.GetManifestResourceStream("InfluenceMods.tsv");
        if (s == null) return (imp, exp);
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            var p = line.Split('\t');
            if (p.Length < 3) continue;
            var who = p[1].Split(',').Select(x => Enum.TryParse<Influence>(x, out var v) ? v : (Influence?)null).OfType<Influence>().ToArray();
            (p[0] == "i" ? imp : exp)[p[2]] = who;
        }
        return (imp, exp);
    }

    /// <summary>
    /// The master of this line among the item's own influences; null when the line is nobody's or could be either of two
    /// the item has. An item without influences has none.
    /// </summary>
    public static Influence? Of(string text, bool implicitMod, IReadOnlyCollection<Influence> itemInfluences)
    {
        if (itemInfluences.Count == 0) return null;
        var (imp, exp) = Data.Value;
        if (!(implicitMod ? imp : exp).TryGetValue(Shape(text), out var who)) return null;
        var mine = who.Where(itemInfluences.Contains).Distinct().ToList();
        return mine.Count == 1 ? mine[0] : null;
    }

    /// <summary>"While a Unique Enemy is in your Presence, 12% increased Attack Speed" → "#% increased Attack Speed".</summary>
    internal static string Shape(string text)
    {
        text = PresenceRx().Replace(text, "");
        text = RangeRx().Replace(text, "#");
        return NumberRx().Replace(text, "#").Trim();
    }

    // the presence condition is a stat of its own in the game data, with no words in the mod's text
    [GeneratedRegex(@"^While an? (Unique Enemy|Pinnacle Atlas Boss) is in your Presence, ")]
    private static partial Regex PresenceRx();
    [GeneratedRegex(@"\(-?[\d.]+--?[\d.]+\)")]
    private static partial Regex RangeRx();
    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex NumberRx();
}
