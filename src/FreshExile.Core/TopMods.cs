using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>
/// Which lines sell an item, by slot and tier — the star of the comparison. Lists of plain phrases, editable in the settings
/// ("#" stands for a number, case does not matter). Hand-made from what the market pays for: the wiki and poedb tell what can
/// roll where and its tiers, not what it is worth.
/// <list type="bullet">
/// <item>"always": +1 to charges, +levels of gems, immunities… — top whatever the slot or tier;</item>
/// <item>a slot's list: top when its tier is in the upper part (<see cref="Share"/>: a third, a half) of the tiers that mod has
/// on this base (<see cref="ModTiers"/>); tier count unknown — taken as 6 (a third = T1-T2); no tier at all (a plain copy, an
/// implicit, a crafted line) — the slot alone decides.</item>
/// </list>
/// </summary>
public sealed class TopMods
{
    public const string Always = "always";

    /// <summary>Slot key → its name in the settings, in the order shown there.</summary>
    public static readonly IReadOnlyList<(string Key, string Name)> Slots =
    [
        (Always, Loc.T("Всегда")), ("boots", Loc.T("Ботинки")), ("gloves", Loc.T("Перчатки")), ("helmet", Loc.T("Шлем")), ("chest", Loc.T("Нагрудник")),
        ("shield", Loc.T("Щит")), ("quiver", Loc.T("Колчан")), ("amulet", Loc.T("Амулет")), ("ring", Loc.T("Кольцо")), ("belt", Loc.T("Пояс")),
        ("weapon", Loc.T("Оружие")), ("jewel", Loc.T("Самоцвет")), ("abyss", Loc.T("Абисс")), ("other", Loc.T("Прочее")),
    ];

    const string Life = "maximum Life";
    const string Suppress = "chance to Suppress Spell Damage";
    const string Chaos = "to Chaos Resistance";
    const string Multi = "Critical Strike Multiplier";
    const string Dot = "Damage over Time Multiplier";
    const string AddsPhys = "Adds # to # Physical Damage to Attacks";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Defaults = new Dictionary<string, IReadOnlyList<string>>
    {
        [Always] =
        [
            "to maximum Power Charges", "to maximum Frenzy Charges", "to maximum Endurance Charges",
            "to Level of all", "to Level of Socketed", "Immun", "Unaffected by", "Corrupted Blood",
            "Cannot be Frozen", "Cannot be Ignited", "Cannot be Shocked", "Cannot be Chilled", "Cannot be Poisoned", "Cannot be Stunned",
            "Elusive", "Onslaught", "Malediction",
        ],
        ["boots"] = ["increased Movement Speed", Suppress, Life, Chaos, "Action Speed"],
        ["gloves"] = ["increased Attack Speed", Suppress, Life, Chaos, AddsPhys, Multi, "Accuracy Rating"],
        ["helmet"] = [Suppress, Life, Chaos, "Mana Reservation Efficiency", "increased maximum Energy Shield", "Accuracy Rating"],
        ["chest"] = [Suppress, Life, Chaos, "increased maximum Life", "increased maximum Energy Shield", "Mana Reservation Efficiency"],
        ["shield"] = [Suppress, Life, Chaos, "increased Spell Damage", "Critical Strike Chance for Spells", "Chance to Block"],
        ["quiver"] = [Multi, "increased Attack Speed", Life, "Damage with Bow Skills", AddsPhys],
        ["amulet"] =
        [
            Multi, Life, Chaos, Dot, "to all Attributes", "increased maximum Energy Shield", "Mana Reservation Efficiency",
            "Elemental Damage with Attack Skills", "Global Critical Strike Chance",
        ],
        ["ring"] = [Life, Chaos, AddsPhys, "Elemental Damage with Attack Skills", "to all Attributes", "increased Attack Speed", "increased Cast Speed", "Mana Regeneration"],
        ["belt"] = [Life, Chaos, "Flasks applied to you have", "Flask Charges", "to Strength", "Elemental Damage with Attack Skills"],
        ["weapon"] =
        [
            "increased Physical Damage", "Adds # to # Physical Damage", "Adds # to # Fire Damage", "Adds # to # Cold Damage",
            "Adds # to # Lightning Damage", "Adds # to # Chaos Damage", "increased Attack Speed", "increased Critical Strike Chance",
            Multi, "increased Spell Damage", "increased Cast Speed", Dot,
        ],
        ["jewel"] = ["increased maximum Life", Multi, Dot, "increased Attack Speed", "increased Cast Speed", Suppress, "increased maximum Energy Shield", "Mana Reservation Efficiency"],
        ["abyss"] = [Life, Multi, Dot, "increased Attack Speed", "increased Cast Speed", Suppress, "increased maximum Energy Shield"],
        ["other"] = [Suppress, "increased Movement Speed", Multi, "to all Attributes", Dot],
    };

    readonly Dictionary<string, Regex> _lists = new();

    /// <summary>3 = the upper third of the mod's tiers, 2 = the upper half, 0 = any tier.</summary>
    public int Share { get; }

    /// <summary>Tiers of a mod when the table does not know it: a third of it is T1-T2.</summary>
    const int UnknownCount = 6;

    /// <param name="edited">The player's lists; a slot missing there keeps its default.</param>
    public TopMods(IReadOnlyDictionary<string, List<string>>? edited = null, int share = 3)
    {
        Share = share;
        foreach (var (key, _) in Slots)
        {
            var phrases = edited != null && edited.TryGetValue(key, out var mine) ? mine : Defaults[key];
            var parts = phrases.Select(p => p.Trim()).Where(p => p.Length > 0)
                .Select(p => Regex.Escape(p).Replace("\\#", @"[+-]?\d+(?:\.\d+)?").Replace("\\ ", " ")).ToList();
            // no phrases: a pattern that never matches
            _lists[key] = new Regex(parts.Count > 0 ? string.Join("|", parts) : "(?!)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    public static readonly TopMods Default = new();

    /// <summary>Trade category ("armour.boots", "weapon.bow"…) → slot key.</summary>
    public static string SlotOf(string? category) => category switch
    {
        "armour.boots" => "boots",
        "armour.gloves" => "gloves",
        "armour.helmet" => "helmet",
        "armour.chest" => "chest",
        "armour.shield" => "shield",
        "armour.quiver" => "quiver",
        "accessory.amulet" => "amulet",
        "accessory.ring" => "ring",
        "accessory.belt" => "belt",
        "jewel.base" => "jewel",
        "jewel.abyss" => "abyss",
        { } c when c.StartsWith("weapon", StringComparison.Ordinal) => "weapon",
        _ => "other",
    };

    /// <summary>The worst tier still in the upper part: a third of 9 tiers = T1-T3, of 2 tiers = T1.</summary>
    public int Limit(int? tierCount) => Share <= 0 ? int.MaxValue : (int)Math.Ceiling((tierCount ?? UnknownCount) / (double)Share);

    /// <param name="tier">From the advanced copy or the trade answer; null when not known.</param>
    /// <param name="tierCount">Tiers the mod has on the base (<see cref="ModTiers.Count"/>); null when not known.</param>
    public bool IsTop(string text, string? category, int? tier, int? tierCount = null)
    {
        if (_lists[Always].IsMatch(text)) return true;
        if (tier > Limit(tierCount)) return false;
        return _lists[SlotOf(category)].IsMatch(text);
    }
}
