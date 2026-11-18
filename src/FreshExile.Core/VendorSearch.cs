namespace FreshExile.Core;

/// <summary>
/// Search strings for the vendor window (it takes a regex, at most 50 characters): one piece per gem that no
/// other gem name contains, joined with "|". Too many gems: several strings, used one after another.
/// Pieces are whole words: the search also reads gem descriptions, and a fragment like "eci" lights up half the shop.
/// Only the clipboard is involved — nothing is typed into the game.
/// </summary>
public static class VendorSearch
{
    public const int MaxLength = 50;

    public static IReadOnlyList<string> Build(IEnumerable<string> wanted, IEnumerable<string> allGemNames, int maxLength = MaxLength)
    {
        var all = allGemNames.Select(n => n.ToLowerInvariant()).Distinct().ToList();
        var pieces = wanted.Select(w => w.ToLowerInvariant().Trim()).Where(w => w.Length > 0).Distinct()
            .Select(w => Piece(w, all)).Distinct().ToList();

        var result = new List<string>();
        var current = new List<string>();
        foreach (var p in pieces)
        {
            if (current.Count > 0 && Quote(current.Append(p)).Length > maxLength)
            {
                result.Add(Quote(current));
                current.Clear();
            }
            current.Add(p);
        }
        if (current.Count > 0) result.Add(Quote(current));
        return result;
    }

    static readonly string[] Filler = ["support", "of", "the", "and"];

    /// <summary>
    /// Shortest whole word of the name that no other gem name contains, then two words in a row;
    /// none — the name without " support" (it then also lights up the gems that contain it).
    /// Vaal and Awakened versions are not sold by vendors and do not count.
    /// </summary>
    public static string Piece(string name, IEnumerable<string> allGemNames)
    {
        name = name.ToLowerInvariant().Trim();
        var bare = name.EndsWith(" support", StringComparison.Ordinal) ? name[..^8] : name;
        var others = allGemNames.Select(n => n.ToLowerInvariant())
            .Where(n => n != name && n != bare && n != bare + " support")
            .Where(n => !n.StartsWith("vaal ", StringComparison.Ordinal) && !n.StartsWith("awakened ", StringComparison.Ordinal))
            .ToList();
        bool Unique(string piece) => !others.Any(o => o.Contains(piece, StringComparison.Ordinal));

        var words = bare.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var single = words.Where(w => w.Length >= 3 && !Filler.Contains(w)).OrderBy(w => w.Length).FirstOrDefault(Unique);
        if (single != null) return single;
        var pair = Enumerable.Range(0, Math.Max(0, words.Length - 1))
            .Select(i => words[i] + " " + words[i + 1]).OrderBy(p => p.Length).FirstOrDefault(Unique);
        return pair ?? bare;
    }

    // spaces need quotes in the game's search; regex specials in gem names are not expected but are escaped anyway
    static string Quote(IEnumerable<string> pieces)
    {
        var body = string.Join("|", pieces.Select(Escape));
        return body.Contains(' ') ? $"\"{body}\"" : body;
    }

    static string Escape(string s) => System.Text.RegularExpressions.Regex.Escape(s).Replace("\\ ", " ");
}

/// <summary>
/// The game log says another character is played than the one the guide follows:
/// a level-up under another name, or the Twilight Strand (only a fresh character starts there).
/// </summary>
public static class NewCharacter
{
    public const string StartArea = "1_1_1";

    /// <param name="hasProgress">
    /// The old character left something behind even on the first stage: "bought" marks, a synced profile.
    /// The Twilight Strand asks then too; on the first stage with nothing to reset it stays silent.
    /// </param>
    /// <returns>The new character's name ("" while not known yet), or null when nothing changed.</returns>
    public static string? Detect(LogEvent e, string? knownCharacter, int stageIndex, bool hasProgress = false) => e switch
    {
        LevelUp l when !string.IsNullOrWhiteSpace(knownCharacter)
                       && !string.Equals(l.Character, knownCharacter.Trim(), StringComparison.OrdinalIgnoreCase) => l.Character,
        AreaGenerated a when (stageIndex > 0 || hasProgress) && string.Equals(a.AreaId, StartArea, StringComparison.OrdinalIgnoreCase) => "",
        _ => null,
    };
}
