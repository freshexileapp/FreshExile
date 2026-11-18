using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

/// <summary>
/// Tests on downloaded data (GGG tree, exile-leveling gems) show up as skipped, with the reason, when the data
/// is not cached on this PC — instead of passing without checking anything. Start the overlay once to download it.
/// </summary>
static class Cache
{
    public static readonly string GemsQuests = Path.Combine(StateStore.Dir, "gems", "quests.json");

    public static string? TreeMissing => File.Exists(TreeRepository.CachePath)
        ? null : $"нет кэша дерева {TreeRepository.CachePath}: запусти оверлей один раз";

    public static string? GemsMissing => File.Exists(GemsQuests)
        ? null : $"нет кэша гемов {GemsQuests}: запусти оверлей один раз";
}

public sealed class TreeFactAttribute : FactAttribute
{
    public TreeFactAttribute() => Skip = Cache.TreeMissing;
}

public sealed class TreeTheoryAttribute : TheoryAttribute
{
    public TreeTheoryAttribute() => Skip = Cache.TreeMissing;
}

public sealed class GemDataFactAttribute : FactAttribute
{
    public GemDataFactAttribute() => Skip = Cache.GemsMissing;
}
