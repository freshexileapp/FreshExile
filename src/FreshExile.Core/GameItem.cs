using System.Globalization;
using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>A mod line of an item copied in game: the text with plain numbers, plus what Ctrl+Alt+C adds (tier, roll range).</summary>
public sealed record GameMod(string Text, ModKind Kind)
{
    /// <summary>"T3" from the advanced copy's header; null in a plain copy or for an implicit / unique line.</summary>
    public int? Tier { get; init; }

    /// <summary>The affix's own name ("Agile") from the advanced copy's header; null in a plain copy.</summary>
    public string? Name { get; init; }

    /// <summary>true = prefix, false = suffix, null = not told (plain copy, implicit, unique line).</summary>
    public bool? Prefix { get; init; }

    /// <summary>"38-42": the roll range of the first number, advanced copy only.</summary>
    public string? Range { get; init; }

    /// <summary>
    /// Which modifier of the advanced copy the line belongs to (its "{ … }" header, counted from 1): a hybrid affix gives
    /// two lines with one number. Null in a plain copy.
    /// </summary>
    public int? Affix { get; init; }

    /// <summary>The first number of the line ("+40 to Dexterity" → 40); "Adds 5 to 10" averages both, as the trade site does.</summary>
    public double? Value => GameItem.ValueOf(Text);
}

/// <summary>A mercenary's skill on a warrant and the supports linked to it: "Holy Strike" with "Hallow" tier 3.</summary>
public sealed record MercenarySkill(string Name, IReadOnlyList<MercenarySupport> Supports);

/// <summary>"Greater Multistrike (Tier: 3)" → ("Greater Multistrike", 3); the trade site lists it as "Greater Multistrike (Tier 3)".</summary>
public sealed record MercenarySupport(string Name, int Tier)
{
    public string TradeText => $"{Name} (Tier {Tier})";
}

/// <summary>A Mercenary Warrant: who ("Karrak, the Faithful"), the build ("Cardinal"), the level, the skills.</summary>
public sealed record MercenaryWarrant(string Mercenary, string Build, int Level, IReadOnlyList<MercenarySkill> Skills);

/// <summary>
/// An item as the game puts it into the clipboard on Ctrl+C (plain) or Ctrl+Alt+C (advanced, with mod tiers and ranges).
/// Only what a price check needs is read.
/// </summary>
public sealed partial record GameItem
{
    public string ItemClass { get; init; } = "";
    public ItemRarity Rarity { get; init; }
    /// <summary>Gems, currency, divination cards, maps…: "Rarity: Gem" / "Currency" / "Divination Card".</summary>
    public string RarityText { get; init; } = "";
    public string Name { get; init; } = "";
    public string BaseType { get; init; } = "";
    public int ItemLevel { get; init; }
    /// <summary>"Level: 59" under Requirements; 0 when the item has none.</summary>
    public int RequiredLevel { get; init; }
    public int Quality { get; init; }
    public string? Sockets { get; init; }
    public int? Armour { get; init; }
    public int? Evasion { get; init; }
    public int? EnergyShield { get; init; }
    public int? Ward { get; init; }
    public int? GemLevel { get; init; }
    public int? MapTier { get; init; }
    /// <summary>A map's own totals from its property block: "Atlas Drops: +71%", "Item Quantity", "Item Rarity", "Monster Pack Size".</summary>
    public int? MapDrops { get; init; }
    public int? MapQuantity { get; init; }
    public int? MapRarity { get; init; }
    public int? MapPackSize { get; init; }
    /// <summary>"Area Level: 80" of a heist contract (and a map's own line).</summary>
    public int? AreaLevel { get; init; }
    /// <summary>A contract's job, "Requires Agility (Level 1)" → (Agility, 1).</summary>
    public string? HeistJob { get; init; }
    public int HeistJobLevel { get; init; }
    public bool Corrupted { get; init; }
    public bool Mirrored { get; init; }
    public bool Unidentified { get; init; }
    public bool FracturedItem { get; init; }
    /// <summary>A Foulborn (mutated) unique: priced apart from the plain one.</summary>
    public bool Foulborn { get; init; }
    public IReadOnlyList<GameMod> Mods { get; init; } = [];
    /// <summary>A Mercenary Warrant's build and skills; null for any other item.</summary>
    public MercenaryWarrant? Warrant { get; init; }

    public string Title => Name.Length > 0 ? Name : BaseType;
    public bool IsGem => RarityText.Equals("Gem", StringComparison.OrdinalIgnoreCase);
    public bool HasDefences => Armour != null || Evasion != null || EnergyShield != null || Ward != null;

    /// <summary>Sockets as the trade site counts links: "R-R-G B" → 3.</summary>
    public int Links => Sockets == null ? 0 : Sockets.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(g => g.Split('-').Length).DefaultIfEmpty(0).Max();
    public int SocketCount => Sockets == null ? 0 : Sockets.Count(c => c is 'R' or 'G' or 'B' or 'W' or 'A' or 'D');

    const string Rule = "--------";

    /// <summary>null when the text is not an item copied from the game.</summary>
    /// <summary>
    /// Item text copied from a game client in another language: the game's "--------" rules are there, the English
    /// "Rarity:" line is not ("Редкость:", "Seltenheit:"…). The price check reads the English client only, for now.
    /// </summary>
    public static bool IsOtherLanguage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains(Rule, StringComparison.Ordinal)) return false;
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Take(4).ToList();
        return !lines.Any(l => l.StartsWith("Rarity:", StringComparison.Ordinal) || l.StartsWith("Item Class:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Prefixes and suffixes the item has and can have: rare 3 + 3 (a jewel 2 + 2), magic 1 + 1, counted by the advanced
    /// copy's modifier headers (crafted and fractured ones take a place too). Null when it cannot be told: a plain copy,
    /// a unique or normal item, a cluster jewel (its notables follow other rules).
    /// </summary>
    public (int Prefixes, int Suffixes, int Max)? Affixes()
    {
        if (Rarity is not (ItemRarity.Rare or ItemRarity.Magic) || Unidentified) return null;
        if (BaseType.Contains("Cluster Jewel", StringComparison.Ordinal)) return null;
        var explicitMods = Mods.Where(m => m.Kind is ModKind.Explicit or ModKind.Crafted or ModKind.Fractured).ToList();
        // a plain copy does not say which side a line is on
        if (explicitMods.Count == 0 ? !Mods.Any(m => m.Affix != null) : explicitMods.Any(m => m.Affix == null || m.Prefix == null)) return null;
        var max = AffixMax();
        var prefixes = explicitMods.Where(m => m.Prefix == true).Select(m => m.Affix).Distinct().Count();
        var suffixes = explicitMods.Where(m => m.Prefix == false).Select(m => m.Affix).Distinct().Count();
        return (prefixes, suffixes, max);
    }

    /// <summary>Prefixes (and as many suffixes) the item can hold: magic 1, a rare jewel 2, any other rare 3.</summary>
    public int AffixMax()
    {
        var jewel = ItemClass is "Jewels" or "Abyss Jewels" || BaseType.EndsWith(" Jewel", StringComparison.Ordinal) || BaseType.EndsWith(" Eye Jewel", StringComparison.Ordinal);
        return Rarity == ItemRarity.Magic ? 1 : jewel ? 2 : 3;
    }

    public static GameItem? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // the game hides zero-width spaces in some lines (U+200B before "Item" in "Level 15 Item Rarity Support"): out, or the trade's text never matches
        var lines = ZeroWidthRx().Replace(text.Replace("\r", ""), "").Split('\n').Select(l => l.Trim()).ToList();
        var sections = new List<List<string>>();
        var cur = new List<string>();
        foreach (var l in lines)
        {
            if (l == Rule) { if (cur.Count > 0) sections.Add(cur); cur = []; continue; }
            if (l.Length > 0) cur.Add(l);
        }
        if (cur.Count > 0) sections.Add(cur);
        if (sections.Count == 0) return null;

        var head = sections[0];
        var h = 0;
        var itemClass = "";
        if (head[h].StartsWith("Item Class:", StringComparison.OrdinalIgnoreCase)) itemClass = head[h++][11..].Trim();
        if (h >= head.Count) return null;
        // some league items (Wickerman eyes and the like) come without a Rarity line: with an Item Class it is still an item
        var hasRarity = head[h].StartsWith("Rarity:", StringComparison.OrdinalIgnoreCase);
        if (!hasRarity && itemClass.Length == 0) return null;
        var rarityText = hasRarity ? head[h++][7..].Trim() : "";
        var rarity = rarityText.ToUpperInvariant() switch
        {
            "UNIQUE" or "RELIC" => ItemRarity.Unique,
            "RARE" => ItemRarity.Rare,
            "MAGIC" => ItemRarity.Magic,
            _ => ItemRarity.Normal,
        };
        var names = head.Skip(h).ToList();
        if (names.Count == 0) return null;
        // rare / unique: name + base; anything else: one line (a magic item's line carries its affixes)
        var name = names.Count >= 2 ? names[0] : rarity == ItemRarity.Magic ? names[0] : "";
        var baseType = names.Count >= 2 ? names[1] : names[0];
        if (rarity == ItemRarity.Magic && names.Count == 1) baseType = "";

        int itemLevel = 0, quality = 0, requiredLevel = 0;
        int? armour = null, evasion = null, es = null, ward = null, gemLevel = null, mapTier = null;
        int? mapDrops = null, mapQuantity = null, mapRarity = null, mapPackSize = null;
        string? sockets = null;
        int? areaLevel = null;
        string? heistJob = null;
        var heistJobLevel = 0;
        bool corrupted = false, mirrored = false, unidentified = false, fracturedItem = false, foulborn = name.StartsWith("Foulborn ", StringComparison.Ordinal);
        var mods = new List<GameMod>();
        var advanced = sections.Any(s => s.Any(l => l.StartsWith('{')));
        var explicitsRead = false;
        var afterItemLevel = false;

        for (var s = 1; s < sections.Count; s++)
        {
            var sec = sections[s];
            var first = sec[0];
            if (first.StartsWith("Requirements", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var l in sec)
                    if (l.StartsWith("Level:", StringComparison.OrdinalIgnoreCase)) requiredLevel = Int(l[6..]) ?? 0;
                continue;
            }
            if (first.StartsWith("Note:", StringComparison.OrdinalIgnoreCase)) continue;
            if (first.StartsWith("Sockets:", StringComparison.OrdinalIgnoreCase)) { sockets = first[8..].Trim(); continue; }
            if (first.StartsWith("Item Level:", StringComparison.OrdinalIgnoreCase))
            {
                itemLevel = Int(first[11..]) ?? 0;
                afterItemLevel = true;
                continue;
            }

            var flags = 0;
            foreach (var l in sec)
                switch (l)
                {
                    // a double corruption (Vaal Orb on a corrupted item, 3.27) reads "Twice Corrupted": corrupted all the same
                    case "Corrupted" or "Twice Corrupted": corrupted = true; flags++; break;
                    case "Mirrored": mirrored = true; flags++; break;
                    case "Unidentified": unidentified = true; flags++; break;
                    case "Fractured Item": fracturedItem = true; flags++; break;
                    case "Foulborn": foulborn = true; flags++; break;
                    case "Split" or "Synthesised Item" or "Shaper Item" or "Elder Item" or "Crusader Item" or "Hunter Item"
                        or "Redeemer Item" or "Warlord Item" or "Searing Exarch Item" or "Eater of Worlds Item":
                        flags++; break;
                }
            if (flags == sec.Count) continue;

            // a heist contract: "Area Level: 80", "Requires Agility (Level 1)"
            foreach (var l in sec)
            {
                if (l.StartsWith("Area Level:", StringComparison.OrdinalIgnoreCase)) areaLevel = Int(l[11..]);
                var j = HeistJobRx().Match(l);
                if (j.Success && heistJob == null) { heistJob = j.Groups[1].Value; heistJobLevel = int.Parse(j.Groups[2].Value, CultureInfo.InvariantCulture); }
            }

            // properties: "Quality: +20% (augmented)", "Armour: 144", "Level: 20 (Max)", "Map Tier: 14"
            var props = 0;
            foreach (var l in sec)
            {
                var m = PropRx().Match(l);
                if (!m.Success) continue;
                var v = Int(m.Groups[2].Value);
                switch (m.Groups[1].Value)
                {
                    case "Quality": quality = v ?? 0; props++; break;
                    case "Armour": armour = v; props++; break;
                    case "Evasion Rating": evasion = v; props++; break;
                    case "Energy Shield": es = v; props++; break;
                    case "Ward": ward = v; props++; break;
                    case "Level": gemLevel = v; props++; break;
                    case "Map Tier": mapTier = v; props++; break;
                    case "Atlas Drops": mapDrops = v; props++; break;
                    case "Item Quantity": mapQuantity = v; props++; break;
                    case "Item Rarity": mapRarity = v; props++; break;
                    case "Monster Pack Size": mapPackSize = v; props++; break;
                    default: props++; break;
                }
            }
            if (props > 0 && !afterItemLevel) continue;

            if (advanced)
            {
                if (first.StartsWith('{')) ReadAdvanced(sec, mods);
                // enchants come without a "{ }" header even in the advanced copy: a cluster's
                // "Adds 8 Passive Skills (enchant)", an amulet's "Allocates Toxic Strikes (enchant)"
                else
                    foreach (var l in sec)
                        if (Marker(l) == ModKind.Enchant)
                            mods.Add(new GameMod(l[..l.LastIndexOf(" (", StringComparison.Ordinal)], ModKind.Enchant));
                continue;
            }

            // plain copy: marked lines are certain; the first unmarked mod section after the item level is the explicits,
            // later unmarked ones are a unique's flavour text or a usage hint
            var marked = sec.Where(l => Marker(l) != null).ToList();
            if (marked.Count > 0)
            {
                foreach (var l in sec)
                {
                    var kind = Marker(l);
                    var t = kind == null ? l : l[..l.LastIndexOf(" (", StringComparison.Ordinal)];
                    if (IsReminder(t)) continue;
                    mods.Add(new GameMod(t, kind ?? ModKind.Explicit));
                }
                if (marked.Any(l => Marker(l) is ModKind.Crafted or ModKind.Fractured) || marked.Count < sec.Count) explicitsRead = true;
                continue;
            }
            if (afterItemLevel && !explicitsRead && !unidentified && rarity is ItemRarity.Magic or ItemRarity.Rare or ItemRarity.Unique)
            {
                foreach (var l in sec)
                    if (!IsReminder(l)) mods.Add(new GameMod(l, ModKind.Explicit));
                explicitsRead = true;
            }
        }

        // the current maps carry no "Map Tier:" line: the base is "Map (Tier 16)"
        if (mapTier == null && MapBaseRx().Match(baseType) is { Success: true } mb) mapTier = int.Parse(mb.Groups[1].Value, CultureInfo.InvariantCulture);

        var warrant = baseType == "Mercenary Warrant" ? ReadWarrant(sections) : null;
        if (warrant != null && warrant.Mercenary.Length > 0) name = warrant.Mercenary;

        return new GameItem
        {
            Warrant = warrant,
            ItemClass = itemClass, Rarity = rarity, RarityText = rarityText, Name = name, BaseType = baseType,
            ItemLevel = itemLevel, RequiredLevel = requiredLevel, Quality = quality, Sockets = sockets,
            Armour = armour, Evasion = evasion, EnergyShield = es, Ward = ward, GemLevel = gemLevel, MapTier = mapTier,
            MapDrops = mapDrops, MapQuantity = mapQuantity, MapRarity = mapRarity, MapPackSize = mapPackSize,
            AreaLevel = areaLevel, HeistJob = heistJob, HeistJobLevel = heistJobLevel,
            Corrupted = corrupted, Mirrored = mirrored, Unidentified = unidentified, FracturedItem = fracturedItem, Foulborn = foulborn, Mods = mods,
        };
    }

    /// <summary>
    /// "Karrak, the Faithful" / "Build: Cardinal", "Mercenary Level: 83" / one section per skill: its name, then its
    /// supports as "Hallow (Tier: 3)"; the usage hint at the end is not a skill.
    /// </summary>
    static MercenaryWarrant ReadWarrant(List<List<string>> sections)
    {
        string mercenary = "", build = "";
        var level = 0;
        var skills = new List<MercenarySkill>();
        for (var i = 1; i < sections.Count; i++)
        {
            var sec = sections[i];
            if (sec.Any(l => l.StartsWith("Build:", StringComparison.Ordinal)))
            {
                foreach (var l in sec)
                {
                    if (l.StartsWith("Build:", StringComparison.Ordinal)) build = l[6..].Trim();
                    else if (l.StartsWith("Mercenary Level:", StringComparison.Ordinal)) level = Int(l[16..]) ?? 0;
                }
                continue;
            }
            if (sec[0].StartsWith("Right click", StringComparison.Ordinal) || sec[0].StartsWith("Open a portal", StringComparison.Ordinal)
                || sec[0].StartsWith("Note:", StringComparison.Ordinal) || sec[0].Contains(':')) continue;
            // the first section is the mercenary's name, unless it already reads like a skill with supports
            if (build.Length == 0 && mercenary.Length == 0 && sec.Count == 1) { mercenary = sec[0]; continue; }
            var supports = sec.Skip(1).Select(l => MercSupportRx().Match(l)).Where(m => m.Success)
                .Select(m => new MercenarySupport(m.Groups[1].Value.Trim(), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
            skills.Add(new MercenarySkill(sec[0], supports));
        }
        return new MercenaryWarrant(mercenary, build, level, skills);
    }

    /// <summary>"{ Prefix Modifier "Agile" (Tier: 3) — Attributes }" then its lines ("+40(38-42) to Dexterity").</summary>
    static void ReadAdvanced(List<string> sec, List<GameMod> mods)
    {
        ModKind kind = ModKind.Explicit;
        int? tier = null;
        string? name = null;
        bool? prefix = null;
        var affix = mods.Select(m => m.Affix ?? 0).DefaultIfEmpty(0).Max();
        foreach (var raw in sec)
        {
            if (raw.StartsWith('{'))
            {
                var hdr = raw;
                affix++;
                var n = AffixNameRx().Match(hdr);
                name = n.Success ? n.Groups[1].Value : null;
                prefix = hdr.Contains("Prefix Modifier", StringComparison.Ordinal) ? true
                    : hdr.Contains("Suffix Modifier", StringComparison.Ordinal) ? false : null;
                kind = hdr.Contains("Implicit", StringComparison.Ordinal) ? ModKind.Implicit
                    : hdr.Contains("Enchant", StringComparison.Ordinal) ? ModKind.Enchant
                    : hdr.Contains("Crafted", StringComparison.Ordinal) ? ModKind.Crafted
                    : hdr.Contains("Fractured", StringComparison.Ordinal) ? ModKind.Fractured
                    : ModKind.Explicit;
                var t = TierRx().Match(hdr);
                tier = t.Success ? int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture) : null;
                continue;
            }
            // "Map contains Al-Hezmin's Citadel(...)": the advanced copy's mark of a hidden value, not part of the text
            var line = raw.Replace(" — Unscalable Value", "", StringComparison.Ordinal).Replace("(...)", "", StringComparison.Ordinal).TrimEnd();
            var marker = Marker(line);
            if (marker != null)
            {
                line = line[..line.LastIndexOf(" (", StringComparison.Ordinal)];
                if (kind == ModKind.Explicit) kind = marker.Value;
            }
            if (IsReminder(line)) continue;
            var range = RangeRx().Match(line);
            var text = RangeRx().Replace(line, "$1");
            mods.Add(new GameMod(text, kind)
            {
                Tier = kind is ModKind.Implicit or ModKind.Enchant ? null : tier,
                Name = kind is ModKind.Implicit or ModKind.Enchant ? null : name,
                Prefix = kind is ModKind.Implicit or ModKind.Enchant ? null : prefix,
                Range = range.Success ? $"{range.Groups[2].Value}-{range.Groups[3].Value}" : null,
                Affix = affix,
            });
        }
    }

    static ModKind? Marker(string l) =>
        l.EndsWith(" (implicit)", StringComparison.Ordinal) ? ModKind.Implicit
        : l.EndsWith(" (enchant)", StringComparison.Ordinal) ? ModKind.Enchant
        : l.EndsWith(" (crafted)", StringComparison.Ordinal) ? ModKind.Crafted
        : l.EndsWith(" (fractured)", StringComparison.Ordinal) ? ModKind.Fractured
        : null;

    /// <summary>A whole line in brackets explains a keyword ("(Bleeding deals …)"): not a mod.</summary>
    static bool IsReminder(string l) => l.StartsWith('(') && l.EndsWith(')');

    static int? Int(string s)
    {
        var m = NumRx().Match(s);
        return m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static double? ValueOf(string text)
    {
        var nums = NumRx().Matches(text).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        if (nums.Count == 0) return null;
        return nums.Count >= 2 && text.StartsWith("Adds ", StringComparison.OrdinalIgnoreCase) ? (nums[0] + nums[1]) / 2 : nums[0];
    }

    #region What the item is worth searching by

    /// <summary>
    /// Armour / evasion / energy shield as they would be at 20% quality: sellers quality their items, and the trade site
    /// compares the shown values. Corrupted and mirrored items cannot be qualitied: their own value stays.
    /// </summary>
    public int? AtFullQuality(int? shown, string defence)
    {
        if (shown is not int v) return null;
        var target = Corrupted || Mirrored ? Quality : Math.Max(Quality, 20);
        if (target == Quality) return v;
        var inc = LocalIncrease(defence);
        return (int)Math.Round(v * (100.0 + inc + target) / (100.0 + inc + Quality));
    }

    /// <summary>"#% increased Armour and Evasion" and the like: local on an armour piece.</summary>
    int LocalIncrease(string defence)
    {
        var sum = 0;
        foreach (var m in Mods)
        {
            if (m.Kind == ModKind.Implicit) continue;
            var x = IncreasedRx().Match(m.Text);
            if (!x.Success) continue;
            var what = x.Groups[2].Value;
            if (what.Contains(defence, StringComparison.OrdinalIgnoreCase)) sum += int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return sum;
    }

    /// <summary>Fire + cold + lightning, the two-element lines twice, "all Elemental" three times.</summary>
    public int TotalElementalResistance => Mods.Sum(m => ResOf(m.Text));

    static int ResOf(string text)
    {
        var m = ResRx().Match(text);
        if (!m.Success) return 0;
        var v = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var what = m.Groups[2].Value;
        if (what.StartsWith("all", StringComparison.OrdinalIgnoreCase)) return v * 3; // "all Elemental" and "all" (chaos too)
        var n = new[] { "Fire", "Cold", "Lightning" }.Count(e => what.Contains(e, StringComparison.OrdinalIgnoreCase));
        return v * n;
    }

    /// <summary>Chaos resistance from every line that gives it ("+X% to Fire and Chaos Resistances", "+X% to all Resistances").</summary>
    public int TotalChaosResistance => Mods.Sum(m => ChaosOf(m.Text));

    static int ChaosOf(string text)
    {
        var m = ResRx().Match(text);
        if (!m.Success) return 0;
        var what = m.Groups[2].Value;
        return what.Contains("Chaos", StringComparison.OrdinalIgnoreCase) || what.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    public static bool IsChaosResistance(string text) => ChaosOf(text) > 0;

    /// <summary>The line counts into <see cref="TotalElementalResistance"/>.</summary>
    public static bool IsElementalResistance(string text) => ResOf(text) > 0;

    /// <summary>Flat life plus half the strength, as the trade site's "total maximum Life" counts it.</summary>
    public int TotalLife
    {
        get
        {
            var life = 0;
            var str = 0;
            foreach (var m in Mods)
            {
                if (LifeRx().Match(m.Text) is { Success: true } l) life += int.Parse(l.Groups[1].Value, CultureInfo.InvariantCulture);
                else if (StrRx().Match(m.Text) is { Success: true } s) str += int.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            return life + str / 2;
        }
    }

    public static bool IsLifeOrStrength(string text) => LifeRx().IsMatch(text) || StrRx().IsMatch(text);

    /// <summary>Flat mana plus half the intelligence, as the trade site's "total maximum Mana" counts it.</summary>
    public int TotalMana
    {
        get
        {
            var mana = 0;
            var intel = 0;
            foreach (var m in Mods)
            {
                if (ManaRx().Match(m.Text) is { Success: true } x) mana += int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);
                else if (IntRx().Match(m.Text) is { Success: true } i) intel += int.Parse(i.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            return mana + intel / 2;
        }
    }

    public static bool IsManaOrIntelligence(string text) => ManaRx().IsMatch(text) || IntRx().IsMatch(text);

    #endregion

    [GeneratedRegex(@"^(Quality|Armour|Evasion Rating|Energy Shield|Ward|Level|Map Tier|Chance to Block|Physical Damage|Elemental Damage|Critical Strike Chance|Attacks per Second|Weapon Range|Stack Size|Atlas Drops|Item Quantity|Item Rarity|Monster Pack Size|Experience|Charges per use|Lasts|Recovers|Consumes)[^:]*:\s*(.*)$")]
    private static partial Regex PropRx();
    [GeneratedRegex(@"\(Tier: (\d+)\)")] private static partial Regex TierRx();
    [GeneratedRegex(@"^Map \(Tier (\d+)\)$")] private static partial Regex MapBaseRx();
    [GeneratedRegex(@"[\u200B-\u200D\u2060\uFEFF]")] private static partial Regex ZeroWidthRx();
    [GeneratedRegex(@"Modifier ""([^""]+)""")] private static partial Regex AffixNameRx();
    [GeneratedRegex(@"^Requires (Lockpicking|Brute Force|Perception|Demolition|Counter-Thaumaturgy|Trap Disarmament|Agility|Deception|Engineering) \(Level (\d+)\)")]
    private static partial Regex HeistJobRx();
    [GeneratedRegex(@"(\d+(?:\.\d+)?)\((-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)\)")] private static partial Regex RangeRx();
    [GeneratedRegex(@"-?\d+(?:\.\d+)?")] private static partial Regex NumRx();
    [GeneratedRegex(@"^(.+?) \(Tier: (\d+)\)$")] private static partial Regex MercSupportRx();
    [GeneratedRegex(@"^(\d+)% increased ((?:Armour|Evasion(?: Rating)?|Energy Shield)(?:,? (?:and )?(?:Armour|Evasion(?: Rating)?|Energy Shield))*)$")]
    private static partial Regex IncreasedRx();
    // "+2% to maximum Fire Resistance" raises the cap, not the resistance: never in the sums
    [GeneratedRegex(@"^\+(\d+)% to (?!.*maximum)(.+) Resistances?$", RegexOptions.IgnoreCase)] private static partial Regex ResRx();
    [GeneratedRegex(@"^\+(\d+) to maximum Life$")] private static partial Regex LifeRx();
    [GeneratedRegex(@"^\+(\d+) to maximum Mana$")] private static partial Regex ManaRx();
    [GeneratedRegex(@"^\+(\d+) to (?:Intelligence(?: and \w+)?|\w+ and Intelligence|all Attributes)$")] private static partial Regex IntRx();
    [GeneratedRegex(@"^\+(\d+) to (?:Strength(?: and \w+)?|\w+ and Strength|all Attributes)$")] private static partial Regex StrRx();
}
