using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FreshExile.Core;

public enum ItemRarity { Normal, Magic, Rare, Unique }

public enum ModKind { Implicit, Explicit, Crafted, Fractured, Enchant }

/// <summary>Who influenced an item: the six of the Atlas ("Shaper Item"…) and the two eldritch ones of the implicits.</summary>
public enum Influence { Shaper, Elder, Crusader, Hunter, Redeemer, Warlord, Exarch, Eater }

public sealed record ItemMod(string Text, ModKind Kind)
{
    /// <summary>The value had a range in PoB ("(40-50)"): worth a minimum on a unique's trade search.</summary>
    public bool Rolled { get; init; }

    /// <summary>
    /// The line of the chosen version of a unique with several (The Light of Meaning: one bonus of many): it is what
    /// tells this copy from the others on the trade site.
    /// </summary>
    public bool Variant { get; init; }

    /// <summary>
    /// Whose influence the line is: an eldritch implicit's master (PoB's "{exarch}" / "{eater}", else by its text), or the
    /// Shaper / Elder / conqueror of an explicit only they give; null for the base's own implicit and a plain mod.
    /// </summary>
    public Influence? Influence { get; init; }
}

/// <summary>One item of the PoB build, its mods resolved to plain lines the game shows.</summary>
public sealed record PobItem(int Id, ItemRarity Rarity, string Name, string BaseType, IReadOnlyList<ItemMod> Mods)
{
    public bool Corrupted { get; init; }
    public string? Sockets { get; init; }
    public int Quality { get; init; }
    public int ItemLevel { get; init; }
    /// <summary>"Shaper Item", "Searing Exarch Item"… lines of the item, in PoB order.</summary>
    public IReadOnlyList<Influence> Influences { get; init; } = [];
    /// <summary>The item's own defences as PoB stores them for an item imported from the game (quality and local mods counted in); null when not written.</summary>
    public int? Armour { get; init; }
    public int? Evasion { get; init; }
    public int? EnergyShield { get; init; }
    public int? Ward { get; init; }

    /// <summary>"Socketed Gems are Supported by Level 25 Divine Blessing" → (Divine Blessing, 25): supports the item gives its sockets.</summary>
    public IEnumerable<(string Name, int Level)> GrantedSupports =>
        Mods.Select(m => PobItems.GrantedRx().Match(m.Text)).Where(x => x.Success)
            .Select(x => (x.Groups[2].Value.Trim(), int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)));

    /// <summary>"+4 to Level of Socketed Aura Gems" and the like.</summary>
    public IEnumerable<string> SocketedBonuses => Mods.Select(m => m.Text).Where(t => PobItems.SocketedLevelRx().IsMatch(t));

    /// <summary>Header line: the name, or the base alone for a white / magic item.</summary>
    public string Title => Name.Length > 0 ? Name : BaseType;

    /// <summary>Close to what Ctrl+C gives in game, without PoB's own markup.</summary>
    /// <param name="itemClass">For the price check: the "Item Class" line and an item level section, as the game writes them.</param>
    public string ToGameText(string? itemClass = null)
    {
        var sb = new System.Text.StringBuilder();
        if (itemClass != null) sb.AppendLine($"Item Class: {itemClass}");
        sb.AppendLine($"Rarity: {Rarity}");
        if (Name.Length > 0 && Name != BaseType) sb.AppendLine(Name);
        sb.AppendLine(BaseType);
        // the properties section, as the game writes it: the price check takes its defence filters from here
        var props = new List<string>();
        if (Quality > 0) props.Add($"Quality: +{Quality}%");
        if (Armour > 0) props.Add($"Armour: {Armour}");
        if (Evasion > 0) props.Add($"Evasion Rating: {Evasion}");
        if (EnergyShield > 0) props.Add($"Energy Shield: {EnergyShield}");
        if (Ward > 0) props.Add($"Ward: {Ward}");
        if (props.Count > 0) { sb.AppendLine("--------"); foreach (var p in props) sb.AppendLine(p); }
        if (Sockets != null) { sb.AppendLine("--------"); sb.AppendLine($"Sockets: {Sockets}"); }
        // the price check reads the unmarked lines after the item level as the explicits
        if (itemClass != null) { sb.AppendLine("--------"); sb.AppendLine($"Item Level: {ItemLevel}"); }
        foreach (var block in new[] { Mods.Where(m => m.Kind == ModKind.Enchant), Mods.Where(m => m.Kind == ModKind.Implicit), Mods.Where(m => m.Kind is ModKind.Explicit or ModKind.Crafted or ModKind.Fractured) })
        {
            var lines = block.ToList();
            if (lines.Count == 0) continue;
            sb.AppendLine("--------");
            foreach (var m in lines)
                sb.AppendLine(m.Kind switch
                {
                    ModKind.Implicit => m.Text + " (implicit)",
                    ModKind.Enchant => m.Text + " (enchant)",
                    ModKind.Crafted => m.Text + " (crafted)",
                    ModKind.Fractured => m.Text + " (fractured)",
                    _ => m.Text,
                });
        }
        if (Corrupted) { sb.AppendLine("--------"); sb.AppendLine("Corrupted"); }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>A PoB item set: slot name ("Helmet", "Ring 1") → item.</summary>
public sealed record ItemSet(string Title, IReadOnlyList<(string Slot, PobItem Item)> Slots);

public static partial class PobItems
{
    /// <summary>Every item of the build by its PoB id.</summary>
    public static IReadOnlyDictionary<int, PobItem> ById(XElement? items)
    {
        var byId = new Dictionary<int, PobItem>();
        if (items == null) return byId;
        foreach (var e in items.Elements("Item"))
        {
            if (!int.TryParse((string?)e.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            // newer PoB puts <ModRange/> children next to the text: take the text nodes only
            var item = ParseItem(id, string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value)));
            if (item != null) byId[id] = item;
        }
        return byId;
    }

    /// <summary>Item sets of the build in PoB order; the active one is reported by index.</summary>
    public static (IReadOnlyList<ItemSet> Sets, int Active) Parse(XElement? items)
    {
        if (items == null) return ([], -1);
        var byId = ById(items);

        var sets = new List<ItemSet>();
        var active = -1;
        var activeId = (string?)items.Attribute("activeItemSet");
        foreach (var s in items.Elements("ItemSet"))
        {
            var slots = new List<(string, PobItem)>();
            foreach (var slot in s.Elements("Slot"))
            {
                var name = (string?)slot.Attribute("name") ?? "";
                // swap weapons, abyss sockets and the like are not what the character wears
                if (name.Length == 0 || name.Contains("Swap", StringComparison.Ordinal) || name.Contains("Abyssal", StringComparison.Ordinal)) continue;
                if (int.TryParse((string?)slot.Attribute("itemId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
                    && byId.TryGetValue(itemId, out var item))
                    slots.Add((name, item));
            }
            if ((string?)s.Attribute("id") == activeId) active = sets.Count;
            sets.Add(new ItemSet(((string?)s.Attribute("title"))?.Trim() ?? "", slots.OrderBy(x => SlotOrder(x.Item1)).ToList()));
        }
        return (sets, active);
    }

    static readonly string[] Order =
        ["Weapon 1", "Weapon 2", "Weapon 1 Swap", "Weapon 2 Swap", "Helmet", "Body Armour", "Gloves", "Boots", "Amulet", "Ring 1", "Ring 2", "Ring 3", "Belt",
         "Flask 1", "Flask 2", "Flask 3", "Flask 4", "Flask 5",
         // the special slots of some items (Greater King Crab's Claw and the like): after the flasks, not at random in the tail
         "Body", "Arm", "Leg"];

    static int SlotOrder(string slot)
    {
        var i = Array.IndexOf(Order, slot);
        return i < 0 ? Order.Length : i;
    }

    public static PobItem? ParseItem(int id, string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return null;

        var rarity = ItemRarity.Normal;
        var i = 0;
        if (lines[0].StartsWith("Rarity:", StringComparison.OrdinalIgnoreCase))
        {
            rarity = lines[0][7..].Trim().ToUpperInvariant() switch
            {
                "UNIQUE" or "RELIC" => ItemRarity.Unique,
                "RARE" => ItemRarity.Rare,
                "MAGIC" => ItemRarity.Magic,
                _ => ItemRarity.Normal,
            };
            i = 1;
        }
        if (i >= lines.Count) return null;

        string name, baseType;
        if (rarity is ItemRarity.Rare or ItemRarity.Unique && i + 1 < lines.Count && !IsMeta(lines[i + 1]))
        {
            name = lines[i];
            baseType = lines[i + 1];
            i += 2;
        }
        else
        {
            name = rarity == ItemRarity.Magic ? lines[i] : "";
            baseType = lines[i];
            i += 1;
        }

        // PoB's placeholder for a rare crafted by hand: no real name, the base says it all
        if (name == "New Item") name = "";

        var selectedVariant = 0;
        var implicits = -1;
        var quality = 0;
        var itemLevel = 0;
        string? sockets = null;
        int? armour = null, evasion = null, es = null, ward = null;
        var corrupted = false;
        var influences = new List<Influence>();
        var mods = new List<ItemMod>();
        var loose = new List<string>();
        for (; i < lines.Count; i++)
        {
            var l = lines[i];
            var meta = MetaRx().Match(l);
            if (meta.Success)
            {
                var key = meta.Groups[1].Value;
                var value = meta.Groups[2].Value.Trim();
                switch (key)
                {
                    case "Selected Variant": int.TryParse(value, out selectedVariant); break;
                    case "Implicits": int.TryParse(value, out implicits); break;
                    case "Quality": int.TryParse(value.TrimStart('+').TrimEnd('%'), out quality); break;
                    case "Item Level": int.TryParse(value, out itemLevel); break;
                    case "Sockets": sockets = value; break;
                    case "Armour": armour = Defence(value); break;
                    case "Evasion": evasion = Defence(value); break;
                    case "Energy Shield": es = Defence(value); break;
                    case "Ward": ward = Defence(value); break;
                }
                // "Implicits: N" opens the mod list: the next N lines are implicits, the rest explicits
                if (key == "Implicits") { ReadMods(lines, i + 1, implicits, selectedVariant, mods, ref corrupted, influences); break; }
                continue;
            }
            if (Flag(l, ref corrupted, influences)) continue;
            loose.Add(l);
        }
        // an item written without "Implicits:" (hand-made in PoB): every plain line is a mod
        if (implicits < 0) ReadMods(loose, 0, 0, selectedVariant, mods, ref corrupted, influences);

        // PoB often leaves the influence unwritten: the mod's text decides; failing that, eldritch implicits replace the
        // base's own, so with one eldritch master on the item every implicit is his
        var eldritch = influences.Where(x => x is Influence.Exarch or Influence.Eater).ToList();
        for (var k = 0; k < mods.Count; k++)
        {
            var m = mods[k];
            if (m.Influence != null || m.Kind is ModKind.Crafted or ModKind.Enchant) continue;
            // the game's mod pools tell whose line it is ("Poisons you inflict deal Damage #% Faster" is the Eater's)
            var who = InfluenceMods.Of(m.Text, m.Kind == ModKind.Implicit, influences)
                ?? (m.Kind == ModKind.Implicit && eldritch.Count == 1 ? eldritch[0] : null);
            if (who != null) mods[k] = m with { Influence = who };
        }

        return new PobItem(id, rarity, name, baseType, mods)
        {
            Corrupted = corrupted, Sockets = sockets, Quality = quality, ItemLevel = itemLevel, Influences = influences.Distinct().ToList(),
            Armour = armour, Evasion = evasion, EnergyShield = es, Ward = ward,
        };
    }

    static void ReadMods(List<string> lines, int from, int implicitCount, int selectedVariant, List<ItemMod> mods, ref bool corrupted, List<Influence> influences)
    {
        var n = 0;
        for (var k = from; k < lines.Count; k++, n++)
        {
            var raw = lines[k];
            if (Flag(raw, ref corrupted, influences)) { n--; continue; }

            var kind = n < implicitCount ? ModKind.Implicit : ModKind.Explicit;
            var range = 0.5;
            var keep = true;
            var variant = false;
            Influence? influence = null;
            var text = raw;
            Match tag;
            while ((tag = TagRx().Match(text)).Success)
            {
                var t = tag.Groups[1].Value;
                var colon = t.IndexOf(':');
                var tName = colon < 0 ? t : t[..colon];
                var tValue = colon < 0 ? "" : t[(colon + 1)..];
                switch (tName)
                {
                    // a crafted implicit is PoB's way to write an enchant: an anointment ("Allocates Toxic Strikes"),
                    // a cluster jewel's "Adds 8 Passive Skills"
                    case "crafted": kind = kind == ModKind.Explicit ? ModKind.Crafted : kind == ModKind.Implicit ? ModKind.Enchant : kind; break;
                    case "fractured": if (kind == ModKind.Explicit) kind = ModKind.Fractured; break;
                    case "enchant": kind = ModKind.Enchant; break;
                    case "exarch": influence = Influence.Exarch; break;
                    case "eater": influence = Influence.Eater; break;
                    case "range": double.TryParse(tValue, NumberStyles.Float, CultureInfo.InvariantCulture, out range); break;
                    case "variant":
                        // a unique with versions: the line belongs to some of them only
                        if (selectedVariant > 0)
                        {
                            keep = tValue.Split(',').Any(v => int.TryParse(v, out var x) && x == selectedVariant);
                            variant = true;
                        }
                        break;
                }
                text = text[tag.Length..];
            }
            if (!keep) continue;
            var rolled = RangeRx().IsMatch(text);
            text = RangeRx().Replace(text, m => Roll(m, range)).Trim();
            if (text.Length == 0 || text.StartsWith("Unique ID", StringComparison.Ordinal)) continue;
            mods.Add(new ItemMod(text, kind) { Rolled = rolled, Variant = variant, Influence = kind == ModKind.Implicit ? influence : null });
        }
    }

    /// <summary>"(10-20)" at PoB's roll position, whole numbers unless the range has decimals.</summary>
    static string Roll(Match m, double range)
    {
        var a = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var b = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var v = a + (b - a) * Math.Clamp(range, 0, 1);
        var decimals = m.Groups[1].Value.Contains('.') || m.Groups[2].Value.Contains('.');
        return decimals ? v.ToString("0.##", CultureInfo.InvariantCulture) : Math.Round(v).ToString(CultureInfo.InvariantCulture);
    }

    static bool Flag(string l, ref bool corrupted, List<Influence> influences)
    {
        switch (l)
        {
            case "Corrupted" or "Twice Corrupted": corrupted = true; return true;
            case "Mirrored" or "Split" or "Unidentified" or "Synthesised Item": return true;
        }
        Influence? x = l switch
        {
            "Shaper Item" => Influence.Shaper,
            "Elder Item" => Influence.Elder,
            "Crusader Item" => Influence.Crusader,
            "Hunter Item" => Influence.Hunter,
            "Redeemer Item" => Influence.Redeemer,
            "Warlord Item" => Influence.Warlord,
            "Searing Exarch Item" => Influence.Exarch,
            "Eater of Worlds Item" => Influence.Eater,
            _ => null,
        };
        if (x is { } v) { influences.Add(v); return true; }
        return false;
    }

    static int? Defence(string v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? (int)Math.Round(d) : null;

    static bool IsMeta(string l) => MetaRx().IsMatch(l);

    [GeneratedRegex(@"^(Unique ID|Item Level|LevelReq|Quality|Sockets|Implicits|Prefix|Suffix|Crafted|Selected Variant|Variant|League|Source|Upgrade|Has Alt Variant\w*|Selected Alt Variant\w*|Armour|Evasion|Energy Shield|Ward|ArmourBasePercentile|EvasionBasePercentile|EnergyShieldBasePercentile|WardBasePercentile|Radius|Limited to|Requires|Catalyst|CatalystQuality|Talisman Tier|Cluster Jewel Skill|Cluster Jewel Node Count|Item Class|Foulborn\w*|Implicit\w*):\s*(.*)$")]
    private static partial Regex MetaRx();

    [GeneratedRegex(@"^\{([^}]*)\}")] private static partial Regex TagRx();
    [GeneratedRegex(@"^Socketed Gems are Supported by Level (\d+) (.+)$", RegexOptions.IgnoreCase)] internal static partial Regex GrantedRx();
    [GeneratedRegex(@"^\+\d+ to Level of Socketed .*Gems$", RegexOptions.IgnoreCase)] internal static partial Regex SocketedLevelRx();
    [GeneratedRegex(@"\((-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)\)")] private static partial Regex RangeRx();
}
