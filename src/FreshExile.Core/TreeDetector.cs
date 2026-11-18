namespace FreshExile.Core;

/// <summary>What one frame says about the in-game passive tree.</summary>
public sealed record TreeVerdict(
    bool TitleSeen,
    AlignResult? Align,
    bool Good,
    bool NewScale,
    double KnownScore,
    bool SweepRan);

/// <summary>
/// Decides from one screenshot whether the in-game tree is open and where it is. Pure: no clock, no UI —
/// the caller says whether the heavy scale sweep is allowed right now (throttling stays outside).
/// </summary>
public static class TreeDetector
{
    /// <summary>Opening needs a clear match; an open overlay survives a weaker frame (pan, hover tooltips).</summary>
    public const double OpenScore = 0.55, KeepScore = 0.4;

    /// <summary>A right fit scores 0.75–0.9; a stale zoom can still hit ~0.5, so below this a sweep is tried.</summary>
    public const double SureScore = 0.7;

    /// <summary>
    /// The same for a structure template (a fresh character, nothing gold yet): its score is the share of links found
    /// minus the share found again a few pixels off, so a right fit scores about 0.5–0.6 at a far zoom.
    /// </summary>
    /// On a closer zoom the links and icons are thicker and a right fit drops to about 0.28; game frames stay under 0.1.
    public const double StructureOpenScore = 0.2, StructureKeepScore = 0.12, StructureSureScore = 0.4;

    /// <summary>
    /// The game's own zoom steps of the passive tree at 1080 px of screen height (seen on real frames and calibrations);
    /// they scale with the height. Tried with the remembered ones, so a zoom change needs no sweep and a recalibration
    /// does not lose them.
    /// </summary>
    static readonly double[] GameZooms1080 = [0.0593, 0.0718, 0.1078, 0.1755, 0.2440, 0.3120, 0.3220, 0.3823, 0.4441];

    /// <summary>The remembered zooms first, then the game's steps for this screen height (near-duplicates dropped).</summary>
    public static IReadOnlyList<double> ZoomsToTry(IEnumerable<double> remembered, int height)
    {
        var list = new List<double>();
        foreach (var z in remembered.Concat(GameZooms1080.Select(z => z * height / 1080.0)))
            if (!list.Any(k => Math.Abs(k - z) / z < 0.008)) list.Add(z);
        return list;
    }

    /// <summary>Mean grey difference of the "Skills" title strip that still counts as the same title.</summary>
    public const double TitleDiff = 14;

    public static TreeVerdict Evaluate(
        TreeAligner aligner, byte[] bgra, int width, int height,
        IReadOnlyList<double> knownScales, byte[]? titlePatch, bool open, bool allowSweep, Action? onSweepStart = null,
        double? currentScale = null)
    {
        var stride = width * 4;
        var openAt = aligner.IsStructure ? StructureOpenScore : OpenScore;
        var keepAt = aligner.IsStructure ? StructureKeepScore : KeepScore;
        var sureAt = aligner.IsStructure ? StructureSureScore : SureScore;

        // the title decides whether the tree is open at all: gameplay frames can fake a path match
        // (gold loot labels, torches), the title strip cannot. Unknown until the first calibration.
        var titleSeen = titlePatch != null && TreeAligner.PatchDiff(TreeAligner.HeaderPatch(bgra, width, height, stride), titlePatch) < TitleDiff;
        if (titlePatch != null && !titleSeen) return new TreeVerdict(false, null, false, false, 0, false);

        // tree already open at a known zoom: that one zoom first (one vote pass instead of one per remembered zoom),
        // the overlay follows a pan several times faster; the full list only when it no longer fits
        AlignResult? r = null;
        if (open && currentScale is double cs && knownScales.Count > 1)
        {
            r = aligner.Align(bgra, width, height, stride, [cs]);
            if (r is not { Score: var cur } || cur < sureAt) r = null;
        }
        r ??= knownScales.Count > 0 ? aligner.Align(bgra, width, height, stride, knownScales) : null;
        var knownScore = r?.Score ?? 0;
        var good = knownScore >= (open ? keepAt : openAt);
        var newScale = false;
        var sweepRan = false;

        // the sweep costs ~2 s; while the open overlay still holds the tree it would only freeze the path, so it waits
        if (knownScore < sureAt && (knownScales.Count == 0 || titleSeen) && allowSweep && !(open && good))
        {
            sweepRan = true;
            onSweepStart?.Invoke();
            if (aligner.Align(bgra, width, height, stride, null) is { } sw && sw.Score >= openAt && sw.Score > knownScore + 0.08)
            {
                r = sw;
                good = newScale = true;
            }
        }
        return new TreeVerdict(titleSeen, r, good, newScale, knownScore, sweepRan);
    }
}
