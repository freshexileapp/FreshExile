namespace FreshExile.Core;

/// <summary>Old → New in the same link; Old only = take out; New only = new gem.</summary>
public sealed record SwapLine(Gem? Old, Gem? New);

public static class GemSwap
{
    /// <summary>
    /// What changes in the sockets from one stage to the next. Each link of the new stage is matched to the
    /// previous link it shares most gems with; inside a link a removed gem is paired with an added one of the
    /// same kind (support ↔ support, active ↔ active). Gems that only moved between links are not listed.
    /// </summary>
    public static List<SwapLine> Diff(Stage prev, Stage cur)
    {
        var prevGroups = prev.Groups.Where(g => g.Gems.Count > 0).ToList();
        var curGroups = cur.Groups.Where(g => g.Gems.Count > 0).ToList();
        var prevAll = prevGroups.SelectMany(g => g.Gems).Select(g => g.Key).ToHashSet();
        var curAll = curGroups.SelectMany(g => g.Gems).Select(g => g.Key).ToHashSet();

        var lines = new List<SwapLine>();
        var usedPrev = new HashSet<int>();
        foreach (var g in curGroups)
        {
            var keys = g.Gems.Select(x => x.Key).ToHashSet();
            var best = -1;
            var bestOverlap = 0;
            for (var i = 0; i < prevGroups.Count; i++)
            {
                if (usedPrev.Contains(i)) continue;
                var overlap = prevGroups[i].Gems.Count(x => keys.Contains(x.Key));
                if (overlap > bestOverlap) { best = i; bestOverlap = overlap; }
            }

            var added = g.Gems.Where(x => !prevAll.Contains(x.Key)).ToList();
            if (best < 0)
            {
                lines.AddRange(added.Select(a => new SwapLine(null, a)));
                continue;
            }
            usedPrev.Add(best);
            var removed = prevGroups[best].Gems.Where(x => !curAll.Contains(x.Key)).ToList();
            Pair(removed, added, lines);
        }

        // links that disappeared completely
        for (var i = 0; i < prevGroups.Count; i++)
            if (!usedPrev.Contains(i))
                lines.AddRange(prevGroups[i].Gems.Where(x => !curAll.Contains(x.Key)).Select(x => new SwapLine(x, null)));

        return lines.DistinctBy(l => (l.Old?.Key, l.New?.Key)).ToList();
    }

    static void Pair(List<Gem> removed, List<Gem> added, List<SwapLine> lines)
    {
        foreach (var support in new[] { true, false })
        {
            var r = removed.Where(x => x.IsSupport == support).ToList();
            var a = added.Where(x => x.IsSupport == support).ToList();
            var n = Math.Max(r.Count, a.Count);
            for (var i = 0; i < n; i++)
                lines.Add(new SwapLine(i < r.Count ? r[i] : null, i < a.Count ? a[i] : null));
        }
    }
}
