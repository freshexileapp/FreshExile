namespace FreshExile.Core;

/// <summary>
/// "How long in this act / on this map" from the game log. Acts: from the first area of the act, its town included;
/// only a higher act starts over — a trip back to an old town for a gem counts to the current act —
/// and the Twilight Strand (a fresh character) starts from act 1 again.
/// Maps: from entering the map; a hideout or town stops the clock on the last value, the same map again
/// (same seed, back through a portal) runs on, a new map starts over. No clock of its own — replayable in tests.
/// </summary>
public sealed class RunTimer
{
    public enum Kind { None, Act, Map }

    int? _act;
    DateTime _actStart;
    string? _mapKey;
    DateTime _mapStart;
    DateTime? _mapStopped;

    public Kind Current { get; private set; }

    public void OnArea(AreaGenerated a, DateTime at)
    {
        if (a.IsMap)
        {
            var key = $"{a.AreaId}#{a.Seed}";
            if (key != _mapKey || a.Seed == 0)
            {
                _mapKey = key;
                _mapStart = at;
            }
            _mapStopped = null;
            Current = Kind.Map;
        }
        else if (a.IsHideout || (a.IsTown && Current == Kind.Map))
        {
            // between maps: keep showing the map that was just run, frozen
            if (Current == Kind.Map) _mapStopped ??= at;
        }
        else if (a.Act is int act)
        {
            var freshCharacter = string.Equals(a.AreaId, NewCharacter.StartArea, StringComparison.OrdinalIgnoreCase);
            if (_act == null || act > _act || freshCharacter)
            {
                _act = act;
                _actStart = at;
            }
            Current = Kind.Act;
        }
    }

    public TimeSpan Elapsed(DateTime now) => Current switch
    {
        Kind.Act => Clamp(now - _actStart),
        Kind.Map => Clamp((_mapStopped ?? now) - _mapStart),
        _ => TimeSpan.Zero,
    };

    public bool Stopped => Current == Kind.Map && _mapStopped != null;

    /// <summary>"акт" / "карта", "" before the first area.</summary>
    public string Label => Current switch { Kind.Act => Loc.T("акт"), Kind.Map => Loc.T("карта"), _ => "" };

    /// <summary>"12:34", "1:02:03", "" before the first area.</summary>
    public string Time(DateTime now) => Current == Kind.None ? "" : Format(Elapsed(now));

    /// <summary>"акт 12:34", "карта 1:02:03", "" before the first area.</summary>
    public string Text(DateTime now) => Current switch
    {
        Kind.Act => Loc.T($"акт {Format(Elapsed(now))}"),
        Kind.Map => Loc.T($"карта {Format(Elapsed(now))}"),
        _ => "",
    };

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";

    static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}
