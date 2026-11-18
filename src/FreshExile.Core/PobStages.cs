using System.Globalization;
using System.Xml.Linq;

namespace FreshExile.Core;

/// <summary>
/// A guide put together from snapshots of a build that keeps changing (a streamer's live PoB): every snapshot's current
/// state (the tree, gem set and item set PoB has open) becomes one more stage, titled by the character's level
/// ("Ур. 38", which the level switch reads). The result is an ordinary PoB XML, marked so the next snapshot appends to it.
/// </summary>
public static class PobStages
{
    const string Mark = "levelingStages";

    public enum Outcome { Started, Appended, Unchanged }

    /// <summary>A guide made of snapshots (the next one is appended to it rather than starting anew).</summary>
    public static bool IsChain(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) return false;
        try { return XDocument.Parse(xml).Root?.Attribute(Mark) != null; }
        catch (System.Xml.XmlException) { return false; }
    }

    /// <summary>
    /// Adds the snapshot's current state to <paramref name="chainXml"/> as the last stage. Starts a new guide when there is
    /// none or it is of another class; leaves it as it is when nothing changed since the last stage.
    /// </summary>
    public static (string Xml, Outcome Outcome, string Title) Append(string? chainXml, string snapshotXml)
    {
        var snap = Load(snapshotXml);
        var piece = Current(snap);

        XDocument? chain = null;
        if (IsChain(chainXml))
        {
            chain = Load(chainXml!);
            var cls = (string?)chain.Root!.Element("Build")?.Attribute("className");
            if (cls != (string?)snap.Root!.Element("Build")?.Attribute("className")) chain = null;
        }

        if (chain == null)
        {
            var doc = new XDocument(new XElement(snap.Root!));
            var root = doc.Root!;
            root.SetAttributeValue(Mark, "1");
            var title = Title(piece.Level, new HashSet<string>());
            root.Element("Tree")?.Remove();
            root.Element("Skills")?.Remove();
            root.Element("Items")?.Remove();
            root.Add(new XElement("Tree", new XAttribute("activeSpec", 1)));
            root.Add(new XElement("Skills", new XAttribute("activeSkillSet", 1)));
            root.Add(new XElement("Items", new XAttribute("activeItemSet", 1)));
            Add(root, piece, title);
            return (doc.ToString(SaveOptions.DisableFormatting), Outcome.Started, title);
        }

        var r = chain.Root!;
        var last = r.Element("Tree")?.Elements("Spec").LastOrDefault();
        var lastSkills = r.Element("Skills")?.Elements("SkillSet").LastOrDefault();
        if (last != null && Same(last, piece.Spec) && SameSkills(lastSkills, piece.Skills))
            return (chainXml!, Outcome.Unchanged, (string?)last.Attribute("title") ?? "");

        var titles = r.Element("Tree")?.Elements("Spec").Select(s => (string?)s.Attribute("title") ?? "").ToHashSet() ?? [];
        var newTitle = Title(piece.Level, titles);
        Add(r, piece, newTitle);
        // the build's own level and the open sets follow the newest stage
        r.Element("Build")?.SetAttributeValue("level", piece.Level);
        var n = r.Element("Tree")!.Elements("Spec").Count();
        r.Element("Tree")!.SetAttributeValue("activeSpec", n);
        r.Element("Skills")!.SetAttributeValue("activeSkillSet", r.Element("Skills")!.Elements("SkillSet").Count());
        r.Element("Items")!.SetAttributeValue("activeItemSet", r.Element("Items")!.Elements("ItemSet").Count());
        return (chain.ToString(SaveOptions.DisableFormatting), Outcome.Appended, newTitle);
    }

    sealed record Piece(int Level, XElement Spec, List<XElement> Skills, XElement? ItemSet, List<XElement> Items);

    static XDocument Load(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            if (doc.Root == null) throw new PobFormatException(Loc.T("Пустой билд"));
            return doc;
        }
        catch (System.Xml.XmlException) { throw new PobFormatException(Loc.T("Билд не читается (битый XML)")); }
    }

    /// <summary>What PoB has open: the active tree, gem set and item set, with the items that set uses.</summary>
    static Piece Current(XDocument doc)
    {
        var root = doc.Root!;
        var level = int.TryParse((string?)root.Element("Build")?.Attribute("level"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 1;

        var tree = root.Element("Tree");
        var specs = tree?.Elements("Spec").ToList() ?? [];
        if (specs.Count == 0) throw new PobFormatException(Loc.T("В билде нет дерева"));
        var si = int.TryParse((string?)tree!.Attribute("activeSpec"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a - 1 : specs.Count - 1;
        var spec = new XElement(specs[Math.Clamp(si, 0, specs.Count - 1)]);

        var skillsEl = root.Element("Skills");
        List<XElement> skills;
        var sets = skillsEl?.Elements("SkillSet").ToList() ?? [];
        if (sets.Count == 0) skills = skillsEl?.Elements("Skill").Select(s => new XElement(s)).ToList() ?? [];
        else
        {
            var id = (string?)skillsEl!.Attribute("activeSkillSet");
            var set = sets.FirstOrDefault(s => (string?)s.Attribute("id") == id) ?? sets[^1];
            skills = set.Elements("Skill").Select(s => new XElement(s)).ToList();
        }

        var itemsEl = root.Element("Items");
        var itemSets = itemsEl?.Elements("ItemSet").ToList() ?? [];
        XElement? itemSet = null;
        var items = new List<XElement>();
        if (itemSets.Count > 0)
        {
            var id = (string?)itemsEl!.Attribute("activeItemSet");
            itemSet = new XElement(itemSets.FirstOrDefault(s => (string?)s.Attribute("id") == id) ?? itemSets[^1]);
            var used = itemSet.Elements("Slot").Select(s => (string?)s.Attribute("itemId"))
                .Concat(spec.Descendants("Socket").Select(s => (string?)s.Attribute("itemId")))
                .Where(x => !string.IsNullOrEmpty(x) && x != "0").ToHashSet();
            items = itemsEl.Elements("Item").Where(i => used.Contains((string?)i.Attribute("id"))).Select(i => new XElement(i)).ToList();
        }
        return new Piece(level, spec, skills, itemSet, items);
    }

    /// <summary>Appends one stage: its tree, its gem set and its item set, all under one title, item ids moved past the existing ones.</summary>
    static void Add(XElement root, Piece p, string title)
    {
        var tree = root.Element("Tree")!;
        var skills = root.Element("Skills")!;
        var items = root.Element("Items")!;

        var offset = items.Elements("Item").Select(i => int.TryParse((string?)i.Attribute("id"), out var v) ? v : 0).DefaultIfEmpty(0).Max();
        string Move(string? id) => int.TryParse(id, out var v) && v > 0 ? (v + offset).ToString(CultureInfo.InvariantCulture) : id ?? "0";

        var spec = new XElement(p.Spec);
        spec.SetAttributeValue("title", title);
        foreach (var s in spec.Descendants("Socket")) s.SetAttributeValue("itemId", Move((string?)s.Attribute("itemId")));
        tree.Add(spec);

        var n = skills.Elements("SkillSet").Count() + 1;
        skills.Add(new XElement("SkillSet", new XAttribute("id", n), new XAttribute("title", title), p.Skills.Select(s => new XElement(s))));

        foreach (var i in p.Items)
        {
            var e = new XElement(i);
            e.SetAttributeValue("id", Move((string?)i.Attribute("id")));
            // items go before the sets, as PoB writes them
            var firstSet = items.Elements("ItemSet").FirstOrDefault();
            if (firstSet != null) firstSet.AddBeforeSelf(e); else items.Add(e);
        }
        var set = p.ItemSet != null ? new XElement(p.ItemSet) : new XElement("ItemSet");
        set.SetAttributeValue("id", items.Elements("ItemSet").Count() + 1);
        set.SetAttributeValue("title", title);
        foreach (var s in set.Elements("Slot")) s.SetAttributeValue("itemId", Move((string?)s.Attribute("itemId")));
        items.Add(set);
    }

    static string Title(int level, ISet<string> taken)
    {
        var t = Loc.T($"Ур. {level}");
        if (!taken.Contains(t)) return t;
        for (var k = 2; ; k++)
            if (!taken.Contains($"{t} ({k})")) return $"{t} ({k})";
    }

    static bool Same(XElement a, XElement b) =>
        Nodes(a).SetEquals(Nodes(b)) && (string?)a.Attribute("masteryEffects") == (string?)b.Attribute("masteryEffects");

    static HashSet<string> Nodes(XElement spec) =>
        ((string?)spec.Attribute("nodes") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    static bool SameSkills(XElement? set, List<XElement> skills)
    {
        static string Gems(IEnumerable<XElement> s) => string.Join("|", s.SelectMany(x => x.Elements("Gem"))
            .Select(g => $"{(string?)g.Attribute("nameSpec")}:{(string?)g.Attribute("level")}:{(string?)g.Attribute("enabled")}"));
        return set != null && Gems(set.Elements("Skill")) == Gems(skills);
    }
}
