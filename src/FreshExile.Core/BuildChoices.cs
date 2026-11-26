using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>The one-off choices a PoB build records: the Act 2 bandits and the pantheon gods.</summary>
public static partial class BuildChoices
{
    static readonly string[] Bandits = ["Alira", "Kraityn", "Oak"];

    /// <summary>The bandits are met in Act 2.</summary>
    public const int BanditAct = 2;

    /// <summary>
    /// The act where the pantheon's first choice comes: the first major god (The Brine King) is freed in Act 6, at the
    /// Brine King's Reef. Fixed here, not guessed from the log.
    /// </summary>
    public const int PantheonAct = 6;

    /// <summary>"помочь Alira — убить Kraityn и Oak"; PoB's "None" is killing all three; null when PoB says nothing.</summary>
    public static string? BanditText(string? bandit)
    {
        if (string.IsNullOrWhiteSpace(bandit)) return null;
        if (bandit.Equals("None", StringComparison.OrdinalIgnoreCase)) return Loc.T("убить всех троих (+2 пассивки)");
        var help = Bandits.FirstOrDefault(b => b.Equals(bandit, StringComparison.OrdinalIgnoreCase));
        if (help == null) return bandit;
        var kill = Bandits.Where(b => b != help).ToArray();
        return Loc.T($"помочь {help} — убить {kill[0]} и {kill[1]}");
    }

    /// <summary>"TheBrineKing" → "The Brine King"; "None"/empty → null.</summary>
    public static string? GodName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
        return WordRx().Replace(id.Trim(), " $1");
    }

    /// <summary>"The Brine King + Ryslatha", either side may be missing; null when both are.</summary>
    public static string? PantheonText(string? major, string? minor)
    {
        var parts = new[] { GodName(major), GodName(minor) }.Where(p => p != null).ToArray();
        return parts.Length == 0 ? null : string.Join(" + ", parts);
    }

    [GeneratedRegex(@"(?<=[a-z])([A-Z])")] private static partial Regex WordRx();
}
