using System.Globalization;
using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>
/// One line of "ours vs theirs": Better &gt; 0 when ours has more, &lt; 0 when theirs does, 0 equal or not comparable;
/// Top for a line that sells on its own on this slot at its tier (<see cref="TopMods"/>), on either side; tiers where known.
/// </summary>
public sealed record CompareRow(string Label, string Ours, string Theirs, int Better)
{
    /// <summary>Top on our item / on theirs (each at its own tier); the star shows for either.</summary>
    public bool OursTop { get; init; }
    public bool TheirsTop { get; init; }
    public bool Top => OursTop || TheirsTop;
    public int? OursTier { get; init; }
    public int? TheirsTier { get; init; }
    /// <summary>How many tiers the mod has on each base ("T3 из 9"); null when the tier table does not know it.</summary>
    public int? OursTierCount { get; init; }
    public int? TheirsTierCount { get; init; }
    /// <summary>The line is fractured on our item / on theirs: it can never be rerolled away.</summary>
    public bool OursFractured { get; init; }
    public bool TheirsFractured { get; init; }
    /// <summary>A total or a defence (not one mod line): such rows lead the list.</summary>
    public bool Total { get; init; }
    /// <summary>A line only the offer has.</summary>
    public bool OnlyTheirs { get; init; }
}

/// <summary>
/// Our item against an offer found on trade, line by line: every filter of the check with a value (ticked or not), counted on
/// the offer the same way (defences at 20% quality, the pseudo totals, a stat by its trade id), then the offer's lines we lack.
/// More is taken as better: true for nearly every stat that sells.
/// </summary>
public static partial class PriceCompare
{
    public static IReadOnlyList<CompareRow> Compare(PriceCheck check, TradeListing offer, TopMods? top = null)
    {
        top ??= TopMods.Default;
        var rows = new List<CompareRow>();
        if (offer.Item is not { } their) return rows;
        var matched = new HashSet<OfferLine>();

        foreach (var f in check.Filters)
        {
            if (f.Value is not double ours) continue;
            var lines = LinesOf(f, offer.Lines);
            matched.UnionWith(lines);
            var theirs = Theirs(f, their, offer.Stats);
            var mod = f.Kind == PriceFilterKind.Stat && !f.IsPseudo;
            var ourCount = ModTiers.Count(check.Item.BaseType, f.ModName, f.Prefix);
            // their best line of the stat: its tier and how many tiers it has on their base
            var best = lines.Where(l => l.Tier != null).MinBy(l => l.Tier);
            rows.Add(new CompareRow(LabelOf(f), Fmt(ours), theirs is double t ? Fmt(t) : "—",
                theirs is double x ? Math.Sign(Math.Round(ours - x, 2)) : 1)
            {
                OursTop = mod && top.IsTop(f.Label, check.Category, f.Tier, ourCount),
                TheirsTop = mod && lines.Any(l => top.IsTop(l.Text, check.Category, l.Tier, CountOf(l, their))),
                OursTier = f.Tier,
                TheirsTier = best?.Tier,
                OursTierCount = ourCount,
                TheirsTierCount = best == null ? null : CountOf(best, their),
                OursFractured = f.ModKind == ModKind.Fractured,
                TheirsFractured = lines.Any(l => l.Kind == ModKind.Fractured),
                Total = !mod,
            });
        }

        // what the offer has and we do not: lines of a stat none of ours is, or (no stat id) of a wording none of ours has
        var ourShapes = check.Item.Mods.Select(m => Shape(m.Text)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in offer.Lines)
        {
            if (matched.Contains(l) || (l.StatId == null && ourShapes.Contains(Shape(l.Text)))) continue;
            rows.Add(new CompareRow(l.Text, "—", GameItem.ValueOf(l.Text) is double v ? Fmt(v) : Loc.T("есть"), -1)
            {
                TheirsTop = top.IsTop(l.Text, check.Category, l.Tier, CountOf(l, their)),
                TheirsTier = l.Tier,
                TheirsTierCount = CountOf(l, their),
                TheirsFractured = l.Kind == ModKind.Fractured,
                OnlyTheirs = true,
            });
        }
        return rows;
    }

    /// <summary>Tiers of an offer's line on the offer's own base.</summary>
    static int? CountOf(OfferLine l, GameItem their) => ModTiers.Count(their.BaseType, l.Name, l.Prefix);

    /// <summary>The offer's lines of this filter's stat: the same id, or the same stat as crafted / fractured / explicit.</summary>
    static List<OfferLine> LinesOf(PriceFilter f, IReadOnlyList<OfferLine> lines)
    {
        if (f.StatId is not { } id || f.IsPseudo) return [];
        var hash = Hash(id);
        var plain = f.ModKind is not (ModKind.Implicit or ModKind.Enchant);
        return lines.Where(l => l.StatId is { } s && (s == id || (plain && Hash(s) == hash && l.Kind is not (ModKind.Implicit or ModKind.Enchant))))
            .ToList();
    }

    static string Hash(string statId) => statId.IndexOf('.') is var dot and >= 0 ? statId[dot..] : statId;

    static double? Theirs(PriceFilter f, GameItem their, IReadOnlyDictionary<string, double> stats)
    {
        switch (f.Kind)
        {
            case PriceFilterKind.Armour: return their.AtFullQuality(their.Armour, "Armour");
            case PriceFilterKind.Evasion: return their.AtFullQuality(their.Evasion, "Evasion");
            case PriceFilterKind.EnergyShield: return their.AtFullQuality(their.EnergyShield, "Energy Shield");
            case PriceFilterKind.Ward: return their.Ward;
        }
        switch (f.StatId)
        {
            case null: return null;
            case "pseudo.pseudo_total_elemental_resistance": return their.TotalElementalResistance;
            case "pseudo.pseudo_total_chaos_resistance": return their.TotalChaosResistance;
            case "pseudo.pseudo_total_life": return their.TotalLife;
            case "pseudo.pseudo_total_mana": return their.TotalMana;
        }
        if (stats.TryGetValue(f.StatId, out var exact)) return exact;
        // a crafted or fractured line of ours is the same stat as a plain explicit one of theirs, and back
        if (f.ModKind is ModKind.Implicit or ModKind.Enchant) return null;
        var hash = Hash(f.StatId);
        double? sum = null;
        foreach (var prefix in new[] { "explicit", "crafted", "fractured" })
            if (stats.TryGetValue(prefix + hash, out var v)) sum = (sum ?? 0) + v;
        return sum;
    }

    /// <summary>"Уклонение: 631 (при 20% качества)" → "Уклонение"; a mod line → its wording with "#" for the numbers.</summary>
    static string LabelOf(PriceFilter f)
    {
        if (f.Kind != PriceFilterKind.Stat || f.IsPseudo)
        {
            var colon = f.Label.IndexOf(':');
            return colon > 0 ? f.Label[..colon] : f.Label;
        }
        return Shape(f.Label);
    }

    static string Shape(string text) => NumRx().Replace(text, "#");

    static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex NumRx();
}
