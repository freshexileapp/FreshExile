using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FreshExile.Core;

public enum PriceFilterKind { Stat, Armour, Evasion, EnergyShield, Ward }

/// <summary>One line of the price check: a stat (or a defence value) with its minimum, ticked or not.</summary>
public sealed class PriceFilter
{
    public required string Label { get; init; }
    public PriceFilterKind Kind { get; init; }
    /// <summary>Trade stat id; null for a defence value or a line the site does not know (shown, never searched).</summary>
    public string? StatId { get; init; }
    public ModKind ModKind { get; init; }
    public bool IsPseudo { get; init; }
    public int? Tier { get; init; }
    /// <summary>The affix's name and side from the advanced copy: how many tiers the mod has (<see cref="ModTiers"/>).</summary>
    public string? ModName { get; init; }
    public bool? Prefix { get; init; }
    public string? Range { get; init; }
    public double? Value { get; init; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public bool Enabled { get; set; }
    /// <summary>The minimum the check starts with (what "exact values" off goes back to).</summary>
    public double? DefaultMin { get; init; }
    /// <summary>On view before "ещё фильтров" is unfolded: the ticked lines, and a unique's rolled ones.</summary>
    public bool Shown { get; set; }
    /// <summary>
    /// Left unticked, yet such a line can make the price on its own (an immunity, a "cannot be" line, +level of gems):
    /// shown with a "!" so the player tries the search with it. Null for an ordinary line.
    /// </summary>
    public string? Hint { get; set; }
    public bool Searchable => Kind != PriceFilterKind.Stat || StatId != null;
}

/// <summary>Price check settings that live in the app's state.</summary>
public sealed record PriceSettings
{
    public string Status { get; init; } = TradeLink.DefaultStatus;
    /// <summary>Default minimum as % of the item's own value.</summary>
    public int Strictness { get; init; } = 90;
    /// <summary>Trade "Buyout Price" option: "" = any (chaos equivalent), "chaos", "divine", "chaos_divine".</summary>
    public string Currency { get; init; } = "";
    /// <summary>Trade "Listed" option: "" = any time, "1day", "3days", "1week", "2weeks".</summary>
    public string Listed { get; init; } = "";
}

/// <summary>
/// What to search for an item copied in game: its filters (pseudo totals, defences at full quality, mods) with sensible
/// ticks, and the trade query those filters make.
/// </summary>
public sealed partial class PriceCheck
{
    public GameItem Item { get; }
    public IReadOnlyList<PriceFilter> Filters { get; }
    /// <summary>Exact base instead of the item's category (always on for uniques, gems and anything without mods).</summary>
    public bool UseBase { get; set; }
    public bool UseItemLevel { get; set; }
    /// <summary>Item flags as the trade site filters them: null = any, true = only such, false = none such.</summary>
    public bool? Corrupted { get; set; }
    public bool? Mirrored { get; set; }
    public bool? Fractured { get; set; }
    /// <summary>true = only unidentified ones (an unid unique sells apart).</summary>
    public bool? Unidentified { get; set; }
    public bool? Foulborn { get; set; }
    /// <summary>Quality at least the item's own.</summary>
    public bool UseQuality { get; set; }
    public bool UseLinks { get; set; }
    /// <summary>Only items of this one's rarity: a magic jewel with two mods is not a rare with the same two and two more.</summary>
    public bool UseRarity { get; set; }
    /// <summary>A heist contract: area level at least its own (83 sells apart from 70).</summary>
    public bool UseAreaLevel { get; set; }
    /// <summary>A heist contract: its job at least its level (a level 5 Agility contract is another thing than level 1).</summary>
    public bool UseHeistJob { get; set; }
    /// <summary>Only what the character can wear now: requirement level at most <see cref="LevelCap"/>.</summary>
    public bool UseLevelCap { get; set; }
    public int LevelCap { get; init; }

    /// <summary>A cluster jewel opened with the notables the build takes: the others may be any that keep off their path.</summary>
    public ClusterPlan? Cluster { get; init; }
    /// <summary>Search the cluster by <see cref="Cluster"/>: the wanted notables, plus any of the harmless others.</summary>
    public bool UseClusterLayout { get; set; }
    public string? Category { get; }

    /// <summary>A gem: level and quality at least these (the window's boxes); null = any. Start at the gem's own.</summary>
    public int? GemLevelMin { get; set; }
    public int? GemQualityMin { get; set; }

    public bool BaseOnly => Category == null || Item.Rarity is ItemRarity.Unique or ItemRarity.Normal || Item.IsGem;

    PriceCheck(GameItem item, IReadOnlyList<PriceFilter> filters, string? category)
    {
        Item = item;
        Filters = filters;
        Category = category;
    }

    /// <summary>
    /// Filters and their ticks, after Awakened PoE Trade's rules (create-stat-filters.ts, pseudo/index.ts):
    /// totals for elemental and chaos resistance and life ticked, mana counted but not ticked; a rare's mod ticked when it is
    /// T1-T2; defences at 20% quality ticked on a single-defence base only; a unique shows its rolled lines (all ticked when
    /// three or fewer) with the minimum taken off the roll range; a corrupted or mirrored magic item at its exact values.
    /// </summary>
    /// <param name="characterLevel">From the game log; 0 = unknown (no "wear it now" filter).</param>
    /// <param name="wantedNotables">A cluster jewel: the notables the build takes (the rest may be other ones).</param>
    public static PriceCheck For(GameItem item, TradeLink trade, int strictness, int characterLevel = 0,
        IReadOnlyCollection<string>? wantedNotables = null)
    {
        // a magic item comes as one line, "Allocated Quicksilver Flask of Bracing": the base is the longest known type in it
        if (item.Rarity == ItemRarity.Magic && item.BaseType.Length == 0 && item.Name.Length > 0 && trade.TypeFor(item.Name) is var b && b != item.Name)
            item = item with { BaseType = b };
        var category = CategoryOf(item.ItemClass);
        var filters = new List<PriceFilter>();
        var rareish = item.Rarity is ItemRarity.Rare or ItemRarity.Magic;
        var unique = item.Rarity == ItemRarity.Unique;
        // a magic item nobody can change any more is worth exactly what it rolled
        var exact = item.Rarity == ItemRarity.Magic && (item.Corrupted || item.Mirrored);
        var spread = 100 - strictness;
        // a small number is searched as it is: 90% of "+1 to Level of all Cold Spell Skill Gems" floors to 0, no filter at all
        double? Min(double? v) => v is double x && x > 0 ? exact || x < 10 ? x : Math.Floor(x * strictness / 100.0) : null;

        void Add(PriceFilter f)
        {
            f.Min ??= f.DefaultMin;
            filters.Add(f);
        }

        if (item.HasDefences && !item.IsGem)
        {
            // a hybrid base is priced by its mods, a pure armour / evasion / ES base by that number
            var single = new[] { item.Armour, item.Evasion, item.EnergyShield, item.Ward }.Count(x => x is > 0) == 1;
            void Def(PriceFilterKind k, int? shown, string name, string label)
            {
                if (item.AtFullQuality(shown, name) is not int v || v <= 0) return;
                var q = item.Corrupted || item.Mirrored ? item.Quality : Math.Max(item.Quality, 20);
                Add(new PriceFilter
                {
                    Label = q != item.Quality ? Loc.T($"{label}: {v} (при {q}% качества)") : $"{label}: {v}", Kind = k, Value = v, DefaultMin = Min(v),
                    Enabled = rareish && single,
                });
            }
            Def(PriceFilterKind.Armour, item.Armour, "Armour", Loc.T("Броня"));
            Def(PriceFilterKind.Evasion, item.Evasion, "Evasion", Loc.T("Уклонение"));
            Def(PriceFilterKind.EnergyShield, item.EnergyShield, "Energy Shield", Loc.T("Энергощит"));
            Def(PriceFilterKind.Ward, item.Ward, "Ward", "Ward");
        }

        var mods = item.Mods.Where(m => m.Kind != ModKind.Enchant || !unique).ToList();
        // a unique's total only when two of its own lines make it up; one line is shown as itself
        bool Enough(int lines) => !unique || lines >= 2;

        var res = item.TotalElementalResistance;
        var resLines = mods.Count(m => GameItem.IsElementalResistance(m.Text));
        var useRes = res > 0 && !item.IsGem && Enough(resLines);
        if (useRes)
            Add(new PriceFilter
            {
                Label = Loc.T($"Сумма сопротивлений стихиям: +{res}%"), StatId = "pseudo.pseudo_total_elemental_resistance", IsPseudo = true,
                Value = res, DefaultMin = Min(res), Enabled = rareish,
            });

        var chaos = item.TotalChaosResistance;
        var chaosLines = mods.Where(m => GameItem.IsChaosResistance(m.Text)).ToList();
        var useChaos = chaos > 0 && !item.IsGem && Enough(chaosLines.Count);
        if (useChaos)
            Add(new PriceFilter
            {
                Label = Loc.T($"Сопротивление хаосу: +{chaos}%"), StatId = "pseudo.pseudo_total_chaos_resistance", IsPseudo = true,
                // a bench-crafted chaos res alone is not worth searching for
                Value = chaos, DefaultMin = Min(chaos), Enabled = rareish && chaosLines.Any(m => m.Kind != ModKind.Crafted),
            });

        var life = item.TotalLife;
        var lifeLines = mods.Count(m => GameItem.IsLifeOrStrength(m.Text));
        var hasLife = mods.Any(m => m.Text.EndsWith("to maximum Life", StringComparison.Ordinal)) && Enough(lifeLines);
        if (life > 0 && hasLife && !item.IsGem)
            Add(new PriceFilter
            {
                Label = Loc.T($"Всего здоровья: +{life}"), StatId = "pseudo.pseudo_total_life", IsPseudo = true,
                Value = life, DefaultMin = Min(life), Enabled = rareish,
            });

        var mana = item.TotalMana;
        var manaLines = mods.Count(m => GameItem.IsManaOrIntelligence(m.Text));
        var hasMana = mods.Any(m => m.Text.EndsWith("to maximum Mana", StringComparison.Ordinal)) && Enough(manaLines);
        if (mana > 0 && hasMana && !item.IsGem)
            Add(new PriceFilter
            {
                Label = Loc.T($"Всего маны: +{mana}"), StatId = "pseudo.pseudo_total_mana", IsPseudo = true,
                Value = mana, DefaultMin = Min(mana), Enabled = false,
            });

        // tiers come with the advanced copy (always, from the hotkey); a plain copy pasted by hand falls back to a list
        var hasTiers = mods.Any(m => m.Tier != null);
        var localSlot = category != null && (category.StartsWith("armour.", StringComparison.Ordinal) || category.StartsWith("weapon", StringComparison.Ordinal));
        // a cluster jewel is its enchant ("Adds 8", sockets, what the small passives grant) and its notables
        var cluster = item.BaseType.EndsWith("Cluster Jewel", StringComparison.OrdinalIgnoreCase);
        var notables = cluster ? mods.Select(m => ClusterNotableRx().Match(m.Text)).Where(x => x.Success).Select(x => x.Groups[1].Value.Trim()).ToList() : [];
        var wanted = wantedNotables is { Count: > 0 } w ? notables.Where(n => w.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList() : notables;
        foreach (var m in mods)
        {
            var kind = cluster && m.Kind is ModKind.Implicit or ModKind.Enchant ? "enchant" : m.Kind switch
            {
                ModKind.Implicit => "implicit",
                ModKind.Crafted => "crafted",
                ModKind.Fractured => "fractured",
                ModKind.Enchant => "enchant",
                _ => "explicit",
            };
            var id = trade.StatId(kind, m.Text, localSlot)
                     ?? (kind is "crafted" or "fractured" ? trade.StatId("explicit", m.Text, localSlot) : null)
                     // the site lists the cluster's sockets only as "# Added Passive Skills are Jewel Sockets"
                     ?? (cluster && ClusterSocketsRx().IsMatch(m.Text) ? trade.StatId(kind, "1 Added Passive Skills are Jewel Sockets", false) : null);
            var covered = (useRes && GameItem.IsElementalResistance(m.Text))
                          || (useChaos && GameItem.IsChaosResistance(m.Text))
                          || (hasLife && GameItem.IsLifeOrStrength(m.Text))
                          || (hasMana && GameItem.IsManaOrIntelligence(m.Text))
                          || (localSlot && item.HasDefences && LocalDefenceRx().IsMatch(m.Text));
            var good = hasTiers ? m.Tier is 1 or 2 : ValuableRx().IsMatch(m.Text);
            var enabled = id != null && rareish && m.Kind is not (ModKind.Implicit or ModKind.Enchant) && !covered && good;
            // a line with no number (an immunity, "cannot be Frozen") or a known big one: not ticked, but flagged.
            // A unique's fixed lines are the same on every copy, so they say nothing about this one's price.
            var hint = id != null && !enabled && !unique && !covered && (m.Value == null || NotableRx().IsMatch(m.Text))
                ? Loc.T("Этот стат может сильно поднять цену — включи его и поищи ещё раз")
                : null;

            var (lo, hi) = ParseRange(m.Range);
            var rolled = lo != null && hi != null && lo != hi;
            double? min;
            double? max = null;
            var seedLine = unique && TimelessJewel.IsSeedLine(m.Text) && id != null;
            if (seedLine)
            {
                // a timeless jewel's seed: exactly this one, another seed changes the tree differently
                min = max = m.Value;
                enabled = true;
            }
            else if (unique && rolled && m.Value is double v)
                // a unique: the minimum comes off the roll range, not off the value; a perfect roll is searched as it is
                min = v >= hi ? v : Math.Max(lo!.Value, Math.Floor(v - (hi!.Value - lo!.Value) * spread / 100.0));
            else
                min = Min(m.Value);

            if (cluster && id != null && ClusterNotableRx().Match(m.Text) is { Success: true } nm)
            {
                // a notable: the ones the build takes; another one is not this jewel.
                // Its "1" is part of the text, not a value: a minimum of 1 would match no listing at all
                enabled = wanted.Contains(nm.Groups[1].Value.Trim(), StringComparer.OrdinalIgnoreCase);
                hint = null;
                min = null;
            }
            else if (cluster && m.Text.StartsWith("Added Small Passive Skills also grant", StringComparison.Ordinal))
                // a small bonus on top of the enchant: seldom what a cluster is bought for
                enabled = false;
            else if (cluster && id != null && kind == "enchant")
            {
                // the size of the ring and its sockets decide where the notables sit: exactly this many passives
                enabled = true;
                if (ClusterAddsRx().IsMatch(m.Text)) { min = m.Value; max = m.Value; }
                else if (!ClusterSocketsRx().IsMatch(m.Text)) min = null;   // "grant: 12% Chaos Damage": the id says it all
            }

            Add(new PriceFilter
            {
                Label = m.Text, StatId = id, ModKind = m.Kind, Tier = m.Tier, ModName = m.Name, Prefix = m.Prefix, Range = m.Range,
                Value = m.Value, DefaultMin = min, Max = max, Enabled = enabled, Hint = hint,
                // a unique's fixed lines say nothing about its price: under the fold; a flagged line stays in view
                Shown = (unique && rolled && m.Kind != ModKind.Implicit && id != null) || hint != null || seedLine,
            });
        }

        // a unique with few rolled lines: all of them count
        if (unique)
        {
            var rolledLines = filters.Where(f => f.Shown).ToList();
            if (rolledLines.Count is > 0 and <= 3) foreach (var f in rolledLines) f.Enabled = true;
        }
        foreach (var f in filters) f.Shown |= f.Enabled;
        // every notable in view, the unticked ones too: they are what the layout chip is about
        if (cluster) foreach (var f in filters.Where(f => ClusterNotableRx().IsMatch(f.Label))) f.Shown = true;

        ClusterPlan? plan = null;
        if (cluster && wanted.Count < notables.Count && wanted.Count > 0)
        {
            var adds = mods.Select(m => ClusterAddsRx().Match(m.Text)).FirstOrDefault(x => x.Success);
            var sockets = mods.Select(m => ClusterSocketsRx().Match(m.Text)).FirstOrDefault(x => x.Success);
            if (adds != null)
                plan = ClusterJewel.Plan(item.BaseType, int.Parse(adds.Groups[1].Value), sockets != null ? int.Parse(sockets.Groups[1].Value) : 0, notables, wanted);
        }

        var check = new PriceCheck(item, filters, category)
        {
            Cluster = plan,
            UseClusterLayout = plan != null,
            LevelCap = characterLevel,
            // levelling: what the character can put on right now; at the end game every item fits anyway
            UseLevelCap = characterLevel is > 0 and < 90 && !item.IsGem && category != null,
            // a corrupted item is compared with corrupted ones; a clean unique or gem with clean ones (corruption changes them)
            Corrupted = item.Corrupted ? true : unique || item.IsGem ? false : null,
            // mirrored copies cannot be changed and sell on their own terms: left out unless this one is mirrored
            Mirrored = item.Mirrored ? true : category != null ? false : null,
            Fractured = item.FracturedItem ? true : null,
            Unidentified = unique && item.Unidentified ? true : null,
            Foulborn = unique ? item.Foulborn : null,
            UseQuality = item.IsGem && item.Quality > 0,
            GemLevelMin = item.IsGem && item.GemLevel is > 1 ? item.GemLevel : null,
            GemQualityMin = item.IsGem && item.Quality > 0 ? item.Quality : null,
            UseLinks = item.Links >= 5,
            UseRarity = item.Rarity == ItemRarity.Magic && category != null,
            UseAreaLevel = item.HeistJob != null && item.AreaLevel > 0,
            UseHeistJob = item.HeistJob != null,
        };
        // a flask is its base (a Quicksilver is not a Granite); armour and jewellery: the slot will do
        check.UseBase = check.BaseOnly || (category == "flask" && item.BaseType.Length > 0);
        return check;
    }

    /// <summary>"38-42" → (38, 42); "-10--5" works too.</summary>
    static (double? Lo, double? Hi) ParseRange(string? range)
    {
        if (range == null) return (null, null);
        var m = RangeRx().Match(range);
        if (!m.Success) return (null, null);
        return (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^(-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)$")] private static partial Regex RangeRx();
    // "1 Added Passive Skill is a Jewel Socket" is the medium cluster's socket, not a notable
    [GeneratedRegex(@"^1 Added Passive Skill is (?!an? Jewel Socket$)(.+)$")] private static partial Regex ClusterNotableRx();
    [GeneratedRegex(@"^Adds (\d+) Passive Skills$")] private static partial Regex ClusterAddsRx();
    [GeneratedRegex(@"^(\d+) Added Passive Skills? (?:are|is an?) Jewel Sockets?$")] private static partial Regex ClusterSocketsRx();

    public JsonObject Query(TradeLink trade, PriceSettings settings)
    {
        var item = Item;
        var query = new JsonObject { ["status"] = new JsonObject { ["option"] = settings.Status } };
        var filters = new JsonObject();

        if (item.Rarity == ItemRarity.Unique && item.Name.Length > 0)
        {
            query["name"] = item.Name;
            query["type"] = item.BaseType;
        }
        else if (item.IsGem) query["type"] = trade.GemType(item.BaseType);
        else if ((UseBase || BaseOnly) && item.BaseType.Length > 0) query["type"] = trade.TypeFor(item.BaseType);
        var typeF = new JsonObject();
        if (query["type"] == null && Category != null) typeF["category"] = Opt(Category);
        // a magic item among magic ones (on request); a rare never among uniques of its base
        if (UseRarity && item.Rarity == ItemRarity.Magic) typeF["rarity"] = Opt("magic");
        else if (item.Rarity == ItemRarity.Rare && Category != null) typeF["rarity"] = Opt("nonunique");
        if (typeF.Count > 0) filters["type_filters"] = Group(typeF);

        var misc = new JsonObject();
        if (UseItemLevel && item.ItemLevel > 0) misc["ilvl"] = new JsonObject { ["min"] = item.ItemLevel };
        void Flag(string id, bool? v) { if (v is bool b) misc[id] = Opt(b ? "true" : "false"); }
        Flag("corrupted", Corrupted);
        Flag("mirrored", Mirrored);
        Flag("fractured_item", Fractured);
        Flag("identified", Unidentified is bool u ? !u : null);
        Flag("mutated", Foulborn);
        if (item.IsGem)
        {
            if (GemLevelMin is int gl and > 1) misc["gem_level"] = new JsonObject { ["min"] = gl };
            if (GemQualityMin is int gq and > 0) misc["quality"] = new JsonObject { ["min"] = gq };
        }
        else if (UseQuality && item.Quality > 0) misc["quality"] = new JsonObject { ["min"] = item.Quality };
        if (misc.Count > 0) filters["misc_filters"] = Group(misc);

        if (UseLevelCap && LevelCap > 0)
            filters["req_filters"] = Group(new JsonObject { ["lvl"] = new JsonObject { ["max"] = LevelCap } });

        if (UseLinks && item.Links >= 5)
            filters["socket_filters"] = Group(new JsonObject { ["links"] = new JsonObject { ["min"] = item.Links } });
        var mapF = new JsonObject();
        if (item.MapTier is int tier) mapF["map_tier"] = new JsonObject { ["min"] = tier, ["max"] = tier };
        if (UseAreaLevel && item.AreaLevel is int area) mapF["area_level"] = new JsonObject { ["min"] = area };
        if (mapF.Count > 0) filters["map_filters"] = Group(mapF);
        // "Counter-Thaumaturgy" → heist_counter_thaumaturgy, "Brute Force" → heist_brute_force
        if (UseHeistJob && item.HeistJob is { } job)
            filters["heist_filters"] = Group(new JsonObject
            {
                ["heist_" + job.ToLowerInvariant().Replace(' ', '_').Replace('-', '_')] = new JsonObject { ["min"] = item.HeistJobLevel },
            });

        var armour = new JsonObject();
        var stats = new JsonArray();
        foreach (var f in Filters.Where(f => f.Enabled && f.Searchable))
        {
            var range = new JsonObject();
            if (f.Min is double mn) range["min"] = mn;
            if (f.Max is double mx) range["max"] = mx;
            switch (f.Kind)
            {
                case PriceFilterKind.Armour: armour["ar"] = range; break;
                case PriceFilterKind.Evasion: armour["ev"] = range; break;
                case PriceFilterKind.EnergyShield: armour["es"] = range; break;
                case PriceFilterKind.Ward: armour["ward"] = range; break;
                default:
                    var stat = new JsonObject { ["id"] = f.StatId };
                    if (range.Count > 0) stat["value"] = range;
                    stats.Add(stat);
                    break;
            }
        }
        if (armour.Count > 0) filters["armour_filters"] = Group(armour);

        var tradeF = new JsonObject();
        if (settings.Currency.Length > 0) tradeF["price"] = Opt(settings.Currency);
        if (settings.Listed.Length > 0) tradeF["indexed"] = Opt(settings.Listed);
        if (tradeF.Count > 0) filters["trade_filters"] = Group(tradeF);

        if (filters.Count > 0) query["filters"] = filters;
        var groups = new JsonArray();
        if (stats.Count > 0) groups.Add(new JsonObject { ["type"] = "and", ["filters"] = stats });
        // the cluster's free places: at least that many of the notables that keep off the wanted ones' path
        if (UseClusterLayout && Cluster is { } plan)
        {
            var others = new JsonArray();
            foreach (var n in plan.Others)
                if (trade.StatId("explicit", "1 Added Passive Skill is " + n, false) is { } id) others.Add(new JsonObject { ["id"] = id });
            if (others.Count > 0)
                groups.Add(new JsonObject { ["type"] = "count", ["value"] = new JsonObject { ["min"] = plan.Count }, ["filters"] = others });
        }
        if (groups.Count > 0) query["stats"] = groups;
        return new JsonObject { ["query"] = query, ["sort"] = new JsonObject { ["price"] = "asc" } };

        static JsonObject Group(JsonObject f) => new() { ["filters"] = f };
        static JsonObject Opt(string v) => new() { ["option"] = v };
    }

    /// <summary>Item Class line → trade category; null for things searched by base alone.</summary>
    public static string? CategoryOf(string itemClass) => itemClass switch
    {
        "Helmets" => "armour.helmet",
        "Body Armours" => "armour.chest",
        "Gloves" => "armour.gloves",
        "Boots" => "armour.boots",
        "Shields" => "armour.shield",
        "Quivers" => "armour.quiver",
        "Amulets" => "accessory.amulet",
        "Rings" => "accessory.ring",
        "Belts" => "accessory.belt",
        "Jewels" => "jewel.base",
        "Abyss Jewels" => "jewel.abyss",
        "Cluster Jewels" => "jewel.cluster",
        "Life Flasks" or "Mana Flasks" or "Hybrid Flasks" or "Utility Flasks" => "flask",
        "Bows" => "weapon.bow",
        "Claws" => "weapon.claw",
        "Daggers" => "weapon.basedagger",
        "Rune Daggers" => "weapon.runedagger",
        "One Hand Axes" => "weapon.oneaxe",
        "One Hand Maces" => "weapon.onemace",
        "One Hand Swords" or "Thrusting One Hand Swords" => "weapon.onesword",
        "Sceptres" => "weapon.sceptre",
        "Staves" => "weapon.basestaff",
        "Warstaves" => "weapon.warstaff",
        "Two Hand Axes" => "weapon.twoaxe",
        "Two Hand Maces" => "weapon.twomace",
        "Two Hand Swords" => "weapon.twosword",
        "Wands" => "weapon.wand",
        _ => null,
    };

    /// <summary>Flat or % defence on an armour piece: already in its armour / evasion / ES value.</summary>
    [GeneratedRegex(@"^(?:\+\d+ to (?:Armour|Evasion Rating|maximum Energy Shield)|\d+% increased (?:Armour|Evasion|Energy Shield).*)$")]
    private static partial Regex LocalDefenceRx();

    /// <summary>Lines that make a rare worth money: ticked by default.</summary>
    [GeneratedRegex(@"Movement Speed|to Level of (?:all|Socketed)|to maximum Energy Shield|increased Spell Damage|increased Physical Damage|Adds \d+ to \d+ (?:Physical|Fire|Cold|Lightning|Chaos) Damage|Critical Strike|Attack Speed|Cast Speed|Chaos Resistance|maximum Mana$|Damage over Time Multiplier|increased Elemental Damage with Attack Skills|Suppress Spell Damage|Life Regeneration|Recoup|Maximum Life", RegexOptions.IgnoreCase)]
    private static partial Regex ValuableRx();

    /// <summary>Lines that are rare and can be worth more than the rest of the item: flagged when left unticked.</summary>
    /// <summary>Whether the line is one of <see cref="NotableRx"/>: the "!" lines, also the always-top mods of the comparison.</summary>
    public static bool IsNotable(string text) => NotableRx().IsMatch(text);

    [GeneratedRegex(@"Corrupted Blood|Immun|cannot be (?:Frozen|Ignited|Shocked|Chilled|Poisoned|Stunned|Inflicted)|to Level of (?:all|Socketed)|maximum (?:Power|Frenzy|Endurance) Charges?|Elusive|Onslaught|Unaffected by|Malediction", RegexOptions.IgnoreCase)]
    private static partial Regex NotableRx();
}
