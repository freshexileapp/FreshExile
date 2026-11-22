namespace FreshExile.Core;

/// <summary>How a catalog name goes to the trade: a unique by name, a gem by its gem type, anything else by base (the exchange's by its id).</summary>
public enum TradeEntryKind { Item, Unique, Gem, Exchange }

/// <summary>
/// One name the trade site sells: "Shaper Guardian Map", "Mageblood Heavy Belt", "Divine Orb"…
/// From /api/trade/data/items (bases, uniques, gems) and /api/trade/data/static (the bulk exchange).
/// </summary>
/// <param name="Group">The site's own group label ("Maps", "Currency"…), shown beside the name.</param>
public sealed record TradeEntry(string Text, string Type, string? Name, string Group, TradeEntryKind Kind)
{
    /// <summary>The item as if copied in game, so the price check takes it the usual way.</summary>
    public GameItem ToItem() => Kind switch
    {
        TradeEntryKind.Unique => new GameItem { Rarity = ItemRarity.Unique, RarityText = "Unique", Name = Name ?? Type, BaseType = Type },
        TradeEntryKind.Gem => new GameItem { Rarity = ItemRarity.Normal, RarityText = "Gem", BaseType = Text },
        _ => new GameItem { Rarity = ItemRarity.Normal, RarityText = "Normal", BaseType = Text },
    };
}

/// <summary>Name search over the catalog: every typed word somewhere in the name, the closest names first.</summary>
public static class TradeCatalog
{
    public static IReadOnlyList<TradeEntry> Find(IEnumerable<TradeEntry> catalog, string? query, int max = 40)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return [];
        var q = string.Join(' ', words);
        return catalog
            .Select(e => (e, rank: Rank(e.Text, q, words)))
            .Where(x => x.rank >= 0)
            .OrderBy(x => x.rank)
            .ThenBy(x => x.e.Text.Length)
            .ThenBy(x => x.e.Text, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.e)
            .ToList();
    }

    /// <summary>0 the very name, 1 starts with what was typed, 2 every word starts a word of the name, 3 inside words; -1 no match.</summary>
    static int Rank(string text, string q, string[] words)
    {
        if (!words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) return -1;
        if (text.Equals(q, StringComparison.OrdinalIgnoreCase)) return 0;
        if (text.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 1;
        return words.All(w => StartsWord(text, w)) ? 2 : 3;
    }

    static bool StartsWord(string text, string w)
    {
        for (var i = text.IndexOf(w, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(w, i + 1, StringComparison.OrdinalIgnoreCase))
            if (i == 0 || !char.IsLetterOrDigit(text[i - 1])) return true;
        return false;
    }
}
