using System.Text;
using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>A mod a map can roll, one row for all its tiers: "Monsters deal (50-110)% extra Physical Damage as Fire".</summary>
public sealed class MapMod
{
    public required int Id { get; init; }
    /// <summary>The game's lines, numbers spread over every tier.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
    public required bool Prefix { get; init; }
    public required bool Suffix { get; init; }
    /// <summary>Rolls on Tier 1-16 maps.</summary>
    public required bool Regular { get; init; }
    /// <summary>Rolls on Tier 17 maps.</summary>
    public required bool Uber { get; init; }

    /// <summary>The shortest piece of the text no other map mod and no other line of a map has.</summary>
    public string Token { get; internal set; } = "";
    /// <summary>Mods whose text contains the whole of this one's (the Tier 17 "charge on Hit, +1 to Maximum"): the token finds them too.</summary>
    public IReadOnlyList<MapMod> AlsoMatches { get; internal set; } = [];

    public string Text => string.Join(Environment.NewLine, Lines);
    public override string ToString() => string.Join(" | ", Lines);
}

/// <summary>What the map regex window asks for.</summary>
public sealed class MapRegexQuery
{
    /// <summary>Not one of these on the map.</summary>
    public ICollection<int> Avoid { get; init; } = new HashSet<int>();
    /// <summary>These on the map: every one (<see cref="WantAll"/>) or any.</summary>
    public ICollection<int> Want { get; init; } = new HashSet<int>();
    public bool WantAll { get; set; }

    /// <summary>The totals of the property block, at least so many percent; null = any.</summary>
    public int? Drops { get; set; }
    public int? Quantity { get; set; }
    public int? Rarity { get; set; }
    public int? PackSize { get; set; }
    public int? Currency { get; set; }
    public int? Scarabs { get; set; }
    public int? Maps { get; set; }
    public int? Cards { get; set; }
    public int? Quality { get; set; }

    /// <summary>null = any, true = only such, false = none such.</summary>
    public bool? Corrupted { get; set; }
    public bool? Unidentified { get; set; }
    /// <summary>The map's own rarity; all off (or all on) = any.</summary>
    public bool Normal { get; set; }
    public bool Magic { get; set; }
    public bool Rare { get; set; }
}

/// <summary>
/// The stash / map device search string for maps, after poe.re: a short piece of each mod's text that nothing else on a
/// map contains, "!" for the ones to avoid, numbers for the totals. The game takes at most 250 characters.
/// Mods from Data/MapMods.tsv, made by tools/update-map-mods.ps1 out of RePoE.
/// </summary>
public static partial class MapRegex
{
    public const int MaxLength = 250;

    static readonly Lazy<IReadOnlyList<MapMod>> Data = new(() => Load(OpenData()));

    public static IReadOnlyList<MapMod> Mods => Data.Value;

    static TextReader? OpenData()
    {
        var s = typeof(MapRegex).Assembly.GetManifestResourceStream("MapMods.tsv");
        return s == null ? null : new StreamReader(s);
    }

    /// <summary>Rows of MapMods.tsv: "p|s|ps  r|u|ru  line | line".</summary>
    public static IReadOnlyList<MapMod> Load(TextReader? r)
    {
        var mods = new List<MapMod>();
        if (r == null) return mods;
        using (r)
            while (r.ReadLine() is { } line)
            {
                var p = line.Split('\t');
                if (p.Length < 3) continue;
                mods.Add(new MapMod
                {
                    Id = mods.Count,
                    Lines = p[2].Split(" | ", StringSplitOptions.RemoveEmptyEntries),
                    Prefix = p[0].Contains('p'), Suffix = p[0].Contains('s'),
                    Regular = p[1].Contains('r'), Uber = p[1].Contains('u'),
                });
            }
        AssignTokens(mods);
        return mods;
    }

    /// <summary>
    /// Everything else a map's text holds, which a token must not catch either: its property block, its footer,
    /// the implicits of influenced and citadel maps.
    /// </summary>
    static readonly string[] OtherLines =
    [
        "item class: maps", "rarity: normal", "rarity: magic", "rarity: rare", "rarity: unique", "map (tier #)", "map tier: #",
        "atlas drops: +#% (augmented)", "item quantity: +#% (augmented)", "item rarity: +#% (augmented)",
        "monster pack size: +#% (augmented)", "more currency: +#% (augmented)", "more scarabs: +#% (augmented)",
        "more maps: +#% (augmented)", "more divination cards: +#% (augmented)", "quality: +#% (augmented)",
        "item level: #", "monster level: #", "corrupted", "unidentified", "mirrored", "fractured item",
        "travel to a map of this tier or lower by using this in a personal map device. maps can only be used once.",
        "area is influenced by the shaper", "area is influenced by the elder", "map contains al-hezmin's citadel",
        "map contains baran's citadel", "map contains drox's citadel", "map contains veritania's citadel",
        "delirium reward type: #", "players in area are #% delirious", "area contains the originator's memories",
    ];

    /// <summary>Lowercase, every number (and its sign and range) as "#".</summary>
    public static string Shape(string line) => NumberRx().Replace(line.ToLowerInvariant(), "#");

    static void AssignTokens(List<MapMod> mods)
    {
        var shapes = mods.Select(m => m.Lines.Select(Shape).ToList()).ToList();
        foreach (var m in mods)
        {
            var own = shapes[m.Id];
            var others = mods.Where(o => o != m).Select(o => (Mod: o, Lines: shapes[o.Id])).ToList();
            bool Free(string piece) => !OtherLines.Any(l => l.Contains(piece, StringComparison.Ordinal))
                                       && !others.Any(o => o.Lines.Any(l => l.Contains(piece, StringComparison.Ordinal)));

            var token = Pieces(own).FirstOrDefault(Free);
            if (token == null)
            {
                // the whole text is in a bigger mod's ("Monsters gain a Frenzy Charge on Hit" in the Tier 17 one with
                // "+1 to Maximum Frenzy Charges"): the piece that leaves out the most others
                token = Pieces(own)
                    .Where(p => !OtherLines.Any(l => l.Contains(p, StringComparison.Ordinal)))
                    .OrderBy(p => others.Count(o => o.Lines.Any(l => l.Contains(p, StringComparison.Ordinal))))
                    .ThenBy(p => p.Length)
                    .FirstOrDefault() ?? own[0].Replace("#", "", StringComparison.Ordinal).Trim();
                var t = token;
                m.AlsoMatches = others.Where(o => o.Lines.Any(l => l.Contains(t, StringComparison.Ordinal))).Select(o => o.Mod).ToList();
            }
            m.Token = token;
        }
    }

    /// <summary>
    /// Every piece of the lines that is plain words (no number, no sign, no bracket), shortest first: the game's search is
    /// a regex, and a "+" or "(" would need escaping. At least three letters, no space at either end.
    /// </summary>
    static IEnumerable<string> Pieces(IReadOnlyList<string> lines)
    {
        var runs = lines.SelectMany(l => NotWordRx().Split(l)).Where(s => s.Length >= 3).Distinct().ToList();
        var longest = runs.Count == 0 ? 0 : runs.Max(s => s.Length);
        for (var len = 3; len <= longest; len++)
            foreach (var run in runs)
                for (var i = 0; i + len <= run.Length; i++)
                {
                    var p = run.Substring(i, len);
                    if (p[0] != ' ' && p[^1] != ' ') yield return p;
                }
    }

    /// <summary>The search string; empty when nothing is asked.</summary>
    public static string Build(MapRegexQuery q, IReadOnlyList<MapMod>? mods = null)
    {
        mods ??= Mods;
        string Tok(int id) => mods[id].Token;
        var parts = new List<string>();

        var avoid = q.Avoid.Where(id => id >= 0 && id < mods.Count).Select(Tok).Distinct().ToList();
        if (avoid.Count > 0) parts.Add("\"!" + string.Join('|', avoid) + "\"");

        var want = q.Want.Where(id => id >= 0 && id < mods.Count && !q.Avoid.Contains(id)).Select(Tok).Distinct().ToList();
        if (want.Count > 0)
        {
            if (q.WantAll) parts.AddRange(want.Select(w => Quote(w)));
            else parts.Add(Quote(string.Join('|', want)));
        }

        // "Item Quantity: +72% (augmented)": the piece before the colon, then the number. A match inside a longer number
        // ("85" of "185") is never less than the piece it was read from, so "at least" holds
        void Total(int? min, string key)
        {
            if (min is int n and > 0) parts.Add(Quote($"{key}.*{AtLeast(n)}%"));
        }
        Total(q.Drops, "drops:");
        Total(q.Quantity, "tity:");
        Total(q.Rarity, "m rarity:");
        Total(q.PackSize, "size:");
        Total(q.Currency, "ncy:");
        Total(q.Scarabs, "abs:");
        Total(q.Maps, "maps:");
        Total(q.Cards, "ards:");
        Total(q.Quality, "lity:");

        if (q.Corrupted is bool c) parts.Add(c ? "corrupted" : "\"!corrupted\"");
        if (q.Unidentified is bool u) parts.Add(u ? "unidentified" : "\"!unidentified\"");

        var rarities = new[] { (q.Normal, "n"), (q.Magic, "m"), (q.Rare, "r") }.Where(x => x.Item1).Select(x => x.Item2).ToList();
        if (rarities.Count is > 0 and < 3) parts.Add(Quote(rarities.Count == 1 ? $"y: {rarities[0]}" : $"y: [{string.Concat(rarities)}]"));

        return string.Join(' ', parts);
    }

    static string Quote(string s) => s.Contains(' ') || s.Contains('|') || s.Contains('*') ? $"\"{s}\"" : s;

    /// <summary>
    /// A regex for a whole number at least <paramref name="n"/> (up to 999): 85 → "(8[5-9]|9\d|\d{3})".
    /// </summary>
    public static string AtLeast(int n)
    {
        n = Math.Clamp(n, 0, 999);
        if (n == 0) return @"\d+";
        var s = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var alts = new List<string>();
        // the same number of digits, digit by digit: "85" → 8 then [5-9]; or a bigger digit here and anything after
        for (var i = 0; i < s.Length; i++)
        {
            var head = s[..i];
            var d = s[i] - '0';
            var rest = s.Length - i - 1;
            if (i == s.Length - 1) { alts.Add(head + Range(d, 9)); break; }
            if (d < 9) alts.Add(head + Range(d + 1, 9) + Digits(rest));
        }
        // more digits than n
        for (var len = s.Length + 1; len <= 3; len++) alts.Add(len == 3 ? @"\d{3}" : @"[1-9]\d");
        return alts.Count == 1 ? alts[0] : "(" + string.Join('|', alts) + ")";

        static string Range(int lo, int hi) => lo == hi ? $"{lo}" : lo == 0 && hi == 9 ? @"\d" : $"[{lo}-{hi}]";
        static string Digits(int count) => count switch { 0 => "", 1 => @"\d", _ => $@"\d{{{count}}}" };
    }

    [GeneratedRegex(@"[+-]?\(-?[\d.]+--?[\d.]+\)|[+-]?\d+(?:\.\d+)?")] private static partial Regex NumberRx();
    [GeneratedRegex(@"[^a-z ',]")] private static partial Regex NotWordRx();
}
