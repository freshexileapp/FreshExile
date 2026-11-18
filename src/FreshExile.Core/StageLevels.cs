using System.Text.RegularExpressions;

namespace FreshExile.Core;

public static partial class StageLevels
{
    /// <summary>
    /// Character level a stage starts at, read from its title:
    /// "lvl 12-30", "Level 30", "ур. 45", "12-30", "70+". Act titles are handled by acts, not levels.
    /// </summary>
    public static int? FromTitle(string title)
    {
        var t = title.Trim().ToLowerInvariant();
        if (ActRx().IsMatch(t)) return null;

        var m = LvlRx().Match(t);
        if (!m.Success) m = RangeRx().Match(t);
        if (!m.Success) m = PlusRx().Match(t);
        if (!m.Success) return null;

        var v = int.Parse(m.Groups[1].Value);
        return v is >= 1 and <= 100 ? v : null;
    }

    /// <summary>Last stage whose start level is reached; null if no stage has a level.</summary>
    public static int? StageForLevel(IReadOnlyList<int?> startLevels, int level)
    {
        int? best = null;
        for (var i = 0; i < startLevels.Count; i++)
            if (startLevels[i] is int s && s <= level) best = i;
        return best;
    }

    /// <summary>Map tier a stage starts at, from its title: "T11", "tier 14", "Red Maps" (11), "Yellow" (6), "White"/"Early Maps" (1).</summary>
    public static int? TierFromTitle(string title)
    {
        var t = title.Trim().ToLowerInvariant();
        var m = TierRx().Match(t);
        if (m.Success)
        {
            var v = int.Parse(m.Groups[1].Value);
            return v is >= 1 and <= 17 ? v : null;
        }
        if (!t.Contains("map") && !t.Contains("карт")) return null;
        if (t.Contains("red") || t.Contains("красн")) return 11;
        if (t.Contains("yellow") || t.Contains("жёлт") || t.Contains("желт")) return 6;
        if (t.Contains("white") || t.Contains("бел") || t.Contains("early") || t.Contains("ранн")) return 1;
        return null;
    }

    /// <summary>Map area level → tier: 68 = T1 … 83 = T16, 84 = T17. Below 68 it is not a map tier.</summary>
    public static int? TierFromAreaLevel(int areaLevel) =>
        areaLevel >= 68 ? Math.Min(17, areaLevel - 67) : null;

    [GeneratedRegex(@"(?:\bt|\btier\s*|\bтир\s*)(\d{1,2})\b")] private static partial Regex TierRx();
    [GeneratedRegex(@"\b(?:act|акт)\s*\d+")] private static partial Regex ActRx();
    [GeneratedRegex(@"(?:\blvl|\blevel|\blv|\bур(?:овень|\.)?)\s*\.?\s*(\d{1,3})")] private static partial Regex LvlRx();
    [GeneratedRegex(@"(?:^|\s)(\d{1,3})\s*[-–—]\s*\d{1,3}\b")] private static partial Regex RangeRx();
    [GeneratedRegex(@"(?:^|\s)(\d{1,3})\s*\+")] private static partial Regex PlusRx();
}
