namespace FreshExile.Core;

/// <summary>
/// A gem of the build on the trade: the text of the item as the game would copy it, for the price check window, and the
/// level to look for. A gem levels up on the way, so the cheapest of any level will do; an exceptional support
/// (Enlighten, Empower, Enhance) and an awakened gem hardly ever level, so they are looked for at the build's level.
/// </summary>
public static class GemSearch
{
    static readonly HashSet<string> Exceptional = new(StringComparer.OrdinalIgnoreCase)
    {
        "Enlighten Support", "Empower Support", "Enhance Support", "Enlighten", "Empower", "Enhance",
    };

    /// <summary>A gem that hardly levels: bought at the level it is used at.</summary>
    public static bool BoughtAtLevel(string name) =>
        Exceptional.Contains(name) || name.StartsWith("Awakened ", StringComparison.OrdinalIgnoreCase);

    /// <summary>The level to search from: 1 (any) for a gem that levels, the build's own for one that does not.</summary>
    public static int MinLevel(string name, int buildLevel) => BoughtAtLevel(name) ? Math.Max(1, buildLevel) : 1;

    /// <summary>"Rarity: Gem / Enlighten Support / Level: 3": enough for <see cref="GameItem.Parse"/> and the trade query.</summary>
    public static string ItemText(string name, bool support, int level) =>
        $"Item Class: {(support ? "Support Gems" : "Skill Gems")}\nRarity: Gem\n{name}\n--------\nLevel: {Math.Max(1, level)}\n";
}
