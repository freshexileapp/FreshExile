using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>The game asked to move the guide: target stage and a short reason for the player.</summary>
public sealed record StageSwitch(int Stage, string Reason);

/// <summary>
/// Turns Client.txt events into guide state: act, level, zone, "in town", and forward-only stage switches
/// by act, character level, first map and map tier. No UI, no clock — replayable in tests.
/// </summary>
public sealed partial class StageFollower(IReadOnlyList<string> titles)
{
    /// <summary>Stage titles of the current build (replaced when another build is loaded).</summary>
    public IReadOnlyList<string> Titles { get; set; } = titles;

    /// <summary>Per stage: character level it starts at (custom or from the title), null = none.</summary>
    public IReadOnlyList<int?> StartLevels { get; set; } = [];

    /// <summary>Per stage: map tier it starts at, null = none.</summary>
    public IReadOnlyList<int?> StartTiers { get; set; } = [];

    public int? Act { get; private set; }
    public int Level { get; private set; }
    public string? Zone { get; private set; }
    public string? Character { get; private set; }
    public bool InTown { get; private set; }

    /// <summary>Feeds one event; returns a switch when the guide should move forward.</summary>
    public StageSwitch? OnEvent(LogEvent e, int current, bool follow)
    {
        switch (e)
        {
            case LevelUp l:
                Level = l.Level;
                Character = l.Character;
                return follow ? ForLevel(current) : null;

            case AreaGenerated a:
                if (a.Act is int act) Act = act;
                // only a fresh character starts on the Twilight Strand: level 1, whoever levelled last in the log
                if (string.Equals(a.AreaId, NewCharacter.StartArea, StringComparison.OrdinalIgnoreCase))
                {
                    Level = 1;
                    Character = null;
                }
                // hideouts count as town: the player shops there too
                InTown = a.IsTown || a.IsHideout;
                return follow && !a.IsHideout ? ForArea(a, current) : null;

            case ZoneEntered z:
                Zone = z.Name;
                return null;
        }
        return null;
    }

    /// <summary>Re-check the level rule (e.g. after the player edited stage levels).</summary>
    public StageSwitch? ForLevel(int current)
    {
        if (Level <= 0) return null;
        var t = StageLevels.StageForLevel(StartLevels, Level);
        return t is int s && s > current ? new StageSwitch(s, Loc.T($"Ур. {Level}: этап «{Titles[s]}»")) : null;
    }

    StageSwitch? ForArea(AreaGenerated a, int current)
    {
        int? target = null;
        string? why = null;
        if (a.Act is int act && !a.IsMap) target = FindActStage(act);
        else if (a.IsMap)
        {
            target = FirstMapStage(current);
            // a stage with a tier wins once that tier is reached ("Red Maps" from T11)
            if (StageLevels.TierFromAreaLevel(a.AreaLevel) is int tier && StageForTier(tier) is int byTier && byTier > (target ?? -1))
            {
                target = byTier;
                why = $"T{tier}";
            }
        }
        // only forward: going back to an old town for a quest must not reset the guide
        if (target is not int t || t <= current) return null;
        return new StageSwitch(t, why == null ? Loc.T($"{Titles[t]}: этап переключён по игре") : Loc.T($"{why}: этап «{Titles[t]}»"));
    }

    int? FindActStage(int act)
    {
        for (var i = 0; i < Titles.Count; i++)
            if (ActOf(Titles[i]) == act) return i;
        return null;
    }

    /// <summary>First map: the stage right after the last act stage, if the guide is still on the acts.</summary>
    int? FirstMapStage(int current)
    {
        var lastAct = -1;
        for (var i = 0; i < Titles.Count; i++)
            if (ActOf(Titles[i]) != null) lastAct = i;
        if (lastAct < 0 || current > lastAct || lastAct + 1 >= Titles.Count) return null;
        return lastAct + 1;
    }

    int? StageForTier(int tier)
    {
        int? best = null;
        for (var i = 0; i < StartTiers.Count; i++)
            if (StartTiers[i] is int t && t <= tier) best = i;
        return best;
    }

    public static int? ActOf(string title)
    {
        var m = ActRx().Match(title);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"\b(?:act|акт)\s*(\d+)\b", RegexOptions.IgnoreCase)] private static partial Regex ActRx();
}

