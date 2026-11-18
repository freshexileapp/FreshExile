using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FreshExile.Core;

public sealed record Gem(string Name, string SkillId, int Level, int Quality, bool IsSupport, bool Enabled)
{
    /// <summary>"Metadata/Items/Gems/SkillGemPoisonArrow" — same id GGG data uses.</summary>
    public string GemId { get; init; } = "";

    public string Key => GemKey(Name);
    public static string GemKey(string name) => name.Trim().ToLowerInvariant();
}

/// <summary>A socket group; with no gems it is a section header (PoB label like "1-12").</summary>
public sealed record GemGroup(string Label, IReadOnlyList<Gem> Gems)
{
    public bool IsHeader => Gems.Count == 0;

    /// <summary>PoB slot the link sits in ("Boots", "Weapon 1"), "" when not set.</summary>
    public string Slot { get; init; } = "";

    /// <summary>
    /// Gems of the link switched off in PoB: left out of the shopping list, but the links view shows them dimmed —
    /// often only kept out of PoB's numbers (Immortal Call under Cast when Damage Taken) and socketed all the same.
    /// </summary>
    public IReadOnlyList<Gem> Disabled { get; init; } = [];
}

public sealed record TreeSpec(
    string Title,
    IReadOnlyList<int> Nodes,
    IReadOnlyDictionary<int, int> MasteryEffects,
    int ClassId,
    int AscendClassId,
    string TreeVersion)
{
    /// <summary>Jewels PoB put into the tree's sockets: socket node id → item id (sockets left empty are not listed).</summary>
    public IReadOnlyDictionary<int, int> Jewels { get; init; } = new Dictionary<int, int>();
}

public sealed record Stage(string Title, TreeSpec? Tree, IReadOnlyList<GemGroup> Groups)
{
    public IEnumerable<Gem> AllGems => Groups.SelectMany(g => g.Gems);
}

public sealed record Build(string ClassName, string Ascendancy, int Level, IReadOnlyList<Stage> Stages)
{
    /// <summary>PoB's bandit: "Alira", "Kraityn", "Oak", or "None" for killing all three.</summary>
    public string? Bandit { get; init; }
    public string? PantheonMajor { get; init; }
    public string? PantheonMinor { get; init; }

    /// <summary>PoB item sets ("Act 3", "Early Maps"…) and which one PoB had open.</summary>
    public IReadOnlyList<ItemSet> ItemSets { get; init; } = [];
    public int ActiveItemSet { get; init; } = -1;

    /// <summary>Every item of the build by its PoB id: jewels are found here from the tree's sockets.</summary>
    public IReadOnlyDictionary<int, PobItem> Items { get; init; } = new Dictionary<int, PobItem>();

    /// <summary>Jewels socketed into the stage's tree, sockets that are not taken left out, in tree order.</summary>
    public IReadOnlyList<(int Socket, PobItem Item)> JewelsFor(Stage stage) =>
        stage.Tree is not { } t ? []
        : t.Jewels.Where(j => t.Nodes.Contains(j.Key) && Items.ContainsKey(j.Value))
            .Select(j => (j.Key, Items[j.Value])).ToList();

    /// <summary>
    /// Gear for a stage: the set named like it (same rules as gem sets: typos, "Act 7+"), the only set,
    /// or the one PoB had open. Null when the build has no items.
    /// </summary>
    public (ItemSet Set, bool Exact)? GearFor(Stage stage)
    {
        if (ItemSets.Count == 0) return null;
        var i = BuildParser.MatchTitle(ItemSets.Select(s => s.Title).ToList(), stage.Title);
        if (i >= 0) return (ItemSets[i], true);
        if (ItemSets.Count == 1) return (ItemSets[0], true);
        return (ItemSets[ActiveItemSet >= 0 ? ActiveItemSet : ItemSets.Count - 1], false);
    }

    /// <summary>Stable id used to keep "bought" marks per build.</summary>
    public string Id
    {
        get
        {
            var s = $"{ClassName}|{Ascendancy}|{string.Join("|", Stages.Select(x => x.Title))}";
            ulong h = 14695981039346656037;
            foreach (var c in s) { h ^= c; h *= 1099511628211; }
            return h.ToString("x16");
        }
    }
}

public static partial class BuildParser
{
    public static Build Parse(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { throw new PobFormatException(Loc.T("Билд не читается (битый XML)")); }

        var root = doc.Root ?? throw new PobFormatException(Loc.T("Пустой билд"));
        var b = root.Element("Build");
        var cls = (string?)b?.Attribute("className") ?? "";
        var asc = (string?)b?.Attribute("ascendClassName") ?? "";
        if (asc == "None") asc = "";
        var level = ParseInt((string?)b?.Attribute("level"), 1);

        var specs = root.Element("Tree")?.Elements("Spec").Select((e, i) => ParseSpec(e, i)).ToList() ?? [];
        var sets = ParseSkillSets(root.Element("Skills"));
        var (itemSets, activeItemSet) = PobItems.Parse(root.Element("Items"));
        var items = PobItems.ById(root.Element("Items"));
        return new Build(cls, asc, level, Merge(specs, sets))
        {
            ItemSets = itemSets,
            Items = items,
            ActiveItemSet = activeItemSet,
            Bandit = (string?)b?.Attribute("bandit"),
            PantheonMajor = (string?)b?.Attribute("pantheonMajorGod"),
            PantheonMinor = (string?)b?.Attribute("pantheonMinorGod"),
        };
    }

    static TreeSpec ParseSpec(XElement e, int index)
    {
        var title = ((string?)e.Attribute("title"))?.Trim();
        if (string.IsNullOrEmpty(title)) title = index == 0 ? "Default" : $"Tree {index + 1}";

        var nodes = ((string?)e.Attribute("nodes") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1)
            .Where(v => v >= 0)
            .ToList();

        var mastery = new Dictionary<int, int>();
        foreach (Match m in MasteryRx().Matches((string?)e.Attribute("masteryEffects") ?? ""))
            mastery[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

        var jewels = new Dictionary<int, int>();
        foreach (var so in e.Element("Sockets")?.Elements("Socket") ?? [])
            if (ParseInt((string?)so.Attribute("nodeId"), 0) is var node and > 0 && ParseInt((string?)so.Attribute("itemId"), 0) is var item and > 0)
                jewels[node] = item;

        return new TreeSpec(title, nodes, mastery,
            ParseInt((string?)e.Attribute("classId"), 0),
            ParseInt((string?)e.Attribute("ascendClassId"), 0),
            (string?)e.Attribute("treeVersion") ?? "")
        {
            Jewels = jewels,
        };
    }

    static List<(string Title, List<GemGroup> Groups)> ParseSkillSets(XElement? skills)
    {
        var result = new List<(string, List<GemGroup>)>();
        if (skills == null) return result;

        var sets = skills.Elements("SkillSet").ToList();
        if (sets.Count == 0)
        {
            var groups = ParseGroups(skills.Elements("Skill"));
            if (groups.Count > 0) result.Add(("", groups));
            return result;
        }
        foreach (var set in sets)
            result.Add((((string?)set.Attribute("title"))?.Trim() ?? "", ParseGroups(set.Elements("Skill"))));
        return result;
    }

    static List<GemGroup> ParseGroups(IEnumerable<XElement> skills)
    {
        var groups = new List<GemGroup>();
        foreach (var s in skills)
        {
            // gems granted by an item are not bought
            if (s.Attribute("source") != null) continue;

            var label = ((string?)s.Attribute("label"))?.Trim() ?? "";
            var all = s.Elements("Gem").Select(ParseGem).Where(g => g != null).Cast<Gem>().ToList();
            // a gem switched off in PoB is not used on this stage yet (Haste waiting next to Grace until Act 7)
            var gems = all.Where(g => g.Enabled).ToList();
            if (all.Count > 0 && gems.Count == 0) continue;

            if (gems.Count == 0)
            {
                if (!SeparatorRx().IsMatch(label)) groups.Add(new GemGroup(label, []));
                continue;
            }
            groups.Add(new GemGroup(SeparatorRx().IsMatch(label) ? "" : label, gems)
            {
                Slot = ((string?)s.Attribute("slot"))?.Trim() ?? "",
                Disabled = all.Where(g => !g.Enabled).ToList(),
            });
        }
        return groups;
    }

    static Gem? ParseGem(XElement g)
    {
        var skillId = (string?)g.Attribute("skillId") ?? "";
        var name = ((string?)g.Attribute("nameSpec"))?.Trim();
        if (string.IsNullOrEmpty(name)) name = skillId;
        if (string.IsNullOrEmpty(name)) return null;

        var gemId = (string?)g.Attribute("gemId") ?? "";
        var support = skillId.StartsWith("Support", StringComparison.Ordinal) || gemId.Contains("/SupportGem", StringComparison.Ordinal);
        return new Gem(name, skillId,
            ParseInt((string?)g.Attribute("level"), 1),
            ParseInt((string?)g.Attribute("quality"), 0),
            support,
            !string.Equals((string?)g.Attribute("enabled"), "false", StringComparison.OrdinalIgnoreCase))
        {
            GemId = gemId,
        };
    }

    static List<Stage> Merge(List<TreeSpec> specs, List<(string Title, List<GemGroup> Groups)> sets)
    {
        var stages = new List<Stage>();
        var used = new HashSet<int>();

        // one final tree + several gem sets: stages follow the gem sets, all showing that tree
        if (specs.Count == 1 && sets.Count > 1 && FindSet(sets, specs[0].Title) < 0)
            return sets.Select((s, i) => new Stage(string.IsNullOrEmpty(s.Title) ? Loc.T($"Гемы {i + 1}") : s.Title, specs[0], (IReadOnlyList<GemGroup>)s.Groups)).ToList();

        IReadOnlyList<GemGroup> previous = sets.Count == 1 ? sets[0].Groups : [];
        foreach (var spec in specs)
        {
            var i = FindSet(sets, spec.Title);
            if (i >= 0)
            {
                used.Add(i);
                previous = sets[i].Groups;
            }
            // no own gem set: the build keeps using the previous one (e.g. "Act 7+" covers Act 8)
            stages.Add(new Stage(spec.Title, spec, previous));
        }

        if (sets.Count == 1 && specs.Count > 0) return stages;
        for (var i = 0; i < sets.Count; i++)
        {
            if (used.Contains(i)) continue;
            var title = string.IsNullOrEmpty(sets[i].Title) ? Loc.T($"Гемы {i + 1}") : sets[i].Title;
            stages.Add(new Stage(title, null, sets[i].Groups));
        }
        return stages;
    }

    static int FindSet(List<(string Title, List<GemGroup> Groups)> sets, string specTitle) =>
        MatchTitle(sets.Select(s => s.Title).ToList(), specTitle);

    /// <summary>Index of the title matching a stage: exact, a typo apart, or the closest "Act N+" below it; -1 if none.</summary>
    public static int MatchTitle(IReadOnlyList<string> titles, string specTitle)
    {
        var sets = titles.Select(x => (Title: x, 0)).ToList();
        var t = Norm(specTitle);
        var i = sets.FindIndex(s => Norm(s.Title) == t);
        if (i >= 0) return i;

        // typo tolerance: "Heis Runner" ↔ "Heist Runner"
        static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
        i = sets.FindIndex(s => Norm(s.Title).Length > 5 && Digits(Norm(s.Title)) == Digits(t) && Distance(Norm(s.Title), t) <= 2);
        if (i >= 0) return i;

        // "Act 7+" applies to Act 7, 8, 9…
        var act = ActNumber(t);
        if (act == null) return -1;
        var best = -1;
        var bestAct = -1;
        for (var k = 0; k < sets.Count; k++)
        {
            var m = ActPlusRx().Match(Norm(sets[k].Title));
            if (!m.Success) continue;
            var from = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (from <= act && from > bestAct) { best = k; bestAct = from; }
        }
        return best;
    }

    static int? ActNumber(string normTitle)
    {
        var m = ActRx().Match(normTitle);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 3;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    static string Norm(string s) => WsRx().Replace(s.Trim().ToLowerInvariant(), " ");

    static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    [GeneratedRegex(@"\{(\d+),(\d+)\}")] private static partial Regex MasteryRx();
    [GeneratedRegex(@"^[\s\-=_~*.]*$")] private static partial Regex SeparatorRx();
    [GeneratedRegex(@"\s+")] private static partial Regex WsRx();
    [GeneratedRegex(@"^(?:act|акт)\s*(\d+)\s*\+$")] private static partial Regex ActPlusRx();
    [GeneratedRegex(@"\b(?:act|акт)\s*(\d+)\b")] private static partial Regex ActRx();
}
