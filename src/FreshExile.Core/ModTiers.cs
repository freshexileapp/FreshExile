namespace FreshExile.Core;

/// <summary>
/// How many tiers a mod has on a base: the affix's own name ("Hale") names its family (IncreasedLife), and the family's
/// affixes that can roll on this base are its tiers. From Data/ModTiers.tsv, made by tools/update-mod-tiers.ps1 out of RePoE.
/// </summary>
public static class ModTiers
{
    sealed record Base(string Domain, HashSet<int> Tags);
    sealed record Mod(string Family, bool Prefix, string Domain, string Name, (int Tag, int Weight)[] Weights);

    static readonly Lazy<(Dictionary<string, Base> Bases, ILookup<string, Mod> ByName, ILookup<string, Mod> ByFamily)> Data = new(Load);

    static (Dictionary<string, Base>, ILookup<string, Mod>, ILookup<string, Mod>) Load()
    {
        var bases = new Dictionary<string, Base>(StringComparer.OrdinalIgnoreCase);
        var mods = new List<Mod>();
        using var s = typeof(ModTiers).Assembly.GetManifestResourceStream("ModTiers.tsv");
        if (s != null)
        {
            using var r = new StreamReader(s);
            while (r.ReadLine() is { } line)
            {
                var p = line.Split('\t');
                if (p[0] == "B" && p.Length >= 4)
                    bases[p[1]] = new Base(p[2], p[3].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet());
                else if (p[0] == "M" && p.Length >= 6)
                    mods.Add(new Mod(p[1], p[2] == "p", p[3], p[4], p[5].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Split(':')).Select(x => (int.Parse(x[0]), int.Parse(x[1]))).ToArray()));
            }
        }
        return (bases, mods.ToLookup(m => m.Name, StringComparer.OrdinalIgnoreCase), mods.ToLookup(m => m.Family));
    }

    /// <summary>The game's rule: the first of the mod's tags the base has decides; none of them = it does not roll there.</summary>
    static bool Rolls(Mod m, Base b)
    {
        if (m.Domain != b.Domain) return false;
        foreach (var (tag, weight) in m.Weights)
            if (b.Tags.Contains(tag)) return weight > 0;
        return false;
    }

    /// <summary>Tiers of the affix named so on this base; null when the base or the name is unknown (crafted, influenced, a new league's).</summary>
    /// <param name="prefix">From the copy's header or the trade's "P1" / "S1"; null = either.</param>
    public static int? Count(string? baseType, string? affixName, bool? prefix)
    {
        if (string.IsNullOrEmpty(baseType) || string.IsNullOrEmpty(affixName)) return null;
        var (bases, byName, byFamily) = Data.Value;
        if (!bases.TryGetValue(baseType, out var b)) return null;
        int? best = null;
        foreach (var m in byName[affixName])
        {
            if (prefix is bool pf && m.Prefix != pf) continue;
            if (!Rolls(m, b)) continue;
            var n = byFamily[m.Family].Count(x => x.Prefix == m.Prefix && Rolls(x, b));
            if (n > 0 && (best == null || n > best)) best = n;
        }
        return best;
    }
}
