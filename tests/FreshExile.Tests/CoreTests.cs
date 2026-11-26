using System.Text;
using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class AlignerTests
{
    static PassiveTree RealTree()
    {
        using var fs = File.OpenRead(TreeRepository.CachePath);
        return PassiveTree.Load(fs);
    }

    // paints the allocated path in game-like gold onto a dark 1920x1080 frame
    static byte[] Frame(List<(double X, double Y)> pts, double s, double ox, double oy, int w, int h)
    {
        var buf = new byte[w * h * 4];
        for (var i = 0; i < buf.Length; i += 4) { buf[i] = 0x11; buf[i + 1] = 0x0D; buf[i + 2] = 0x09; }
        foreach (var p in pts)
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var x = (int)(p.X * s + ox) + dx; var y = (int)(p.Y * s + oy) + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    var i = (y * w + x) * 4;
                    buf[i] = 0x87; buf[i + 1] = 0xAE; buf[i + 2] = 0xC5;
                }
        return buf;
    }

    [TreeFact]
    public void Finds_scale_and_offset_then_tracks_pan()
    {
        var tree = RealTree();
        var build = BuildParser.Parse(PobCode.DecodeToXml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ranger-pathfinder.txt"))));
        var template = TreeAligner.BuildTemplate(tree, build.Stages[3].Tree!.Nodes);
        var aligner = new TreeAligner(template);
        const int W = 1920, H = 1080;

        var r = aligner.Align(Frame(template, 0.108, 342, 205, W, H), W, H, W * 4, null)!.Value;
        Assert.InRange(r.Scale, 0.105, 0.111);
        Assert.InRange(r.OffsetX, 336, 348);
        Assert.InRange(r.OffsetY, 199, 211);
        Assert.True(r.Score > 0.9);

        var t = aligner.Align(Frame(template, 0.108, 500, 120, W, H), W, H, W * 4, [r.Scale])!.Value;
        Assert.InRange(t.OffsetX, 494, 506);
        Assert.InRange(t.OffsetY, 114, 126);

        // zoomed out: the right one of the remembered zoom levels wins
        var z = aligner.Align(Frame(template, 0.075, 700, 300, W, H), W, H, W * 4, [0.108, 0.075])!.Value;
        Assert.InRange(z.Scale, 0.074, 0.076);
        Assert.True(z.Score > 0.9);

        // unknown zoom with only the old level remembered does not match -> caller sweeps
        var miss = aligner.Align(Frame(template, 0.075, 700, 300, W, H), W, H, W * 4, [0.108]);
        Assert.True(miss == null || miss.Value.Score < 0.55);
        var swept = aligner.Align(Frame(template, 0.075, 700, 300, W, H), W, H, W * 4, null)!.Value;
        Assert.InRange(swept.Scale, 0.073, 0.077);
    }

    [TreeFact]
    public void Node_taken_in_game_is_detected_by_its_gold_edge()
    {
        var tree = RealTree();
        var build = BuildParser.Parse(PobCode.DecodeToXml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ranger-pathfinder.txt"))));
        var have = build.Stages[2].Tree!.Nodes;
        var plan = PathPlanner.Plan(tree, have, build.Stages[3].Tree!.Nodes, build.Stages[3].Tree!.ClassId);
        var first = plan.Order[0];
        var edges = TreeAligner.EdgesByNode(tree);
        const int W = 1920, H = 1080;
        const double S = 0.108;

        // what the game shows after clicking step 1: the allocated path plus one more gold edge
        var template = TreeAligner.BuildTemplate(tree, have);
        var aligner = new TreeAligner(template);
        var withFirst = TreeAligner.BuildTemplate(tree, have.Append(first));

        // put the plan's first node near the middle of the screen
        var n = tree.Nodes[first];
        double ox = W / 2 - n.X * S, oy = H / 2 - n.Y * S;

        var before = aligner.Align(Frame(template, S, ox, oy, W, H), W, H, W * 4, [S])!.Value;
        Assert.Empty(aligner.DetectTaken(tree, plan, edges, before, W, H));

        var after = aligner.Align(Frame(withFirst, S, ox, oy, W, H), W, H, W * 4, [S])!.Value;
        Assert.Equal([first], aligner.DetectTaken(tree, plan, edges, after, W, H));
    }

    [TreeFact]
    public void Node_refunded_in_game_is_detected_by_its_edge_losing_gold()
    {
        var tree = RealTree();
        var build = BuildParser.Parse(PobCode.DecodeToXml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ranger-pathfinder.txt"))));
        // a later tree as "have", an earlier one as the target: the difference is to refund
        var have = build.Stages[3].Tree!.Nodes;
        var plan = PathPlanner.Plan(tree, have, build.Stages[2].Tree!.Nodes, build.Stages[2].Tree!.ClassId);
        var edges = TreeAligner.EdgesByNode(tree);
        // the end of a refunded branch: one allocated neighbour, so taking it off breaks nothing else
        var leaf = plan.Refund.First(id => tree.Nodes[id].Drawable && edges.TryGetValue(id, out var es)
            && es.Count(e => plan.Have.Contains(e.A == id ? e.B : e.A)) == 1);
        const int W = 1920, H = 1080;
        const double S = 0.108;

        var template = TreeAligner.BuildTemplate(tree, have);
        var aligner = new TreeAligner(template);
        var n = tree.Nodes[leaf];
        double ox = W / 2 - n.X * S, oy = H / 2 - n.Y * S;

        var before = aligner.Align(Frame(template, S, ox, oy, W, H), W, H, W * 4, [S])!.Value;
        Assert.DoesNotContain(leaf, aligner.DetectRefunded(tree, plan, edges, before, W, H));

        var respecced = TreeAligner.BuildTemplate(tree, have.Where(id => id != leaf));
        var after = aligner.Align(Frame(respecced, S, ox, oy, W, H), W, H, W * 4, [S])!.Value;
        Assert.Contains(leaf, aligner.DetectRefunded(tree, plan, edges, after, W, H));
    }

    [Fact]
    public void Title_patch_tells_same_screen_from_other()
    {
        const int W = 1920, H = 1080;
        var a = new byte[W * H * 4];
        var b = new byte[W * H * 4];
        for (var i = 0; i < a.Length; i++) { a[i] = (byte)(i % 251); b[i] = (byte)((i * 7) % 253); }
        var pa = TreeAligner.HeaderPatch(a, W, H, W * 4);
        Assert.Equal(0, TreeAligner.PatchDiff(pa, TreeAligner.HeaderPatch(a, W, H, W * 4)));
        Assert.True(TreeAligner.PatchDiff(pa, TreeAligner.HeaderPatch(new byte[W * H * 4], W, H, W * 4)) > 14);
    }

    [TreeFact]
    public void Empty_frame_does_not_match()
    {
        var build = BuildParser.Parse(PobCode.DecodeToXml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ranger-pathfinder.txt"))));
        var aligner = new TreeAligner(TreeAligner.BuildTemplate(RealTree(), build.Stages[3].Tree!.Nodes));
        var r = aligner.Align(Frame([], 0.1, 0, 0, 640, 360), 640, 360, 640 * 4, [0.108]);
        Assert.True(r == null || r.Value.Score < 0.3);
    }
}

public class GemDatabaseTests
{
    const string Gems = """
    {
      "Metadata/Items/Gems/SkillGemPoisonArrow": { "name": "Caustic Arrow", "primary_attribute": "dexterity", "required_level": 1, "is_support": false },
      "Metadata/Items/Gems/SkillGemPoisonArrowRoyale": { "name": "Caustic Arrow", "primary_attribute": "dexterity", "required_level": 4, "is_support": false },
      "Metadata/Items/Gems/SupportGemGreaterVolley": { "name": "Greater Volley Support", "primary_attribute": "dexterity", "required_level": 38, "is_support": true },
      "Metadata/Items/Gems/SkillGemClarity": { "name": "Clarity", "primary_attribute": "intelligence", "required_level": 1, "is_support": false }
    }
    """;

    const string Quests = """
    {
      "a1q1": { "name": "Enemy at the Gate", "act": "1", "reward_offers": { "a1q1": {
          "quest_npc": "Tarkleigh",
          "quest": { "Metadata/Items/Gems/SkillGemPoisonArrow": { "classes": ["Witch"] } },
          "vendor": { "Metadata/Items/Gems/SkillGemPoisonArrow": { "classes": ["Ranger"], "npc": "Nessa" },
                      "Metadata/Items/Gems/SkillGemClarity": { "classes": [], "npc": "Nessa" } } } } },
      "a1q2": { "name": "Mercy Mission", "act": "1", "reward_offers": { "a1q2": {
          "quest_npc": "Nessa",
          "quest": { "Metadata/Items/Gems/SkillGemClarity": { "classes": ["Ranger"] } }, "vendor": {} } } },
      "a4q1": { "name": "The Eternal Nightmare", "act": "4", "reward_offers": { "a4q1": {
          "quest_npc": "Dialla", "quest": {},
          "vendor": { "Metadata/Items/Gems/SupportGemGreaterVolley": { "classes": [], "npc": "Petarus and Vanja" } } } } }
    }
    """;

    static GemDatabase Db() => GemDatabase.Load(new MemoryStream(Encoding.UTF8.GetBytes(Gems)), new MemoryStream(Encoding.UTF8.GetBytes(Quests)));

    static Gem G(string name, string id = "", bool support = false) => new(name, "", 1, 0, support, true) { GemId = id };

    [Fact]
    public void Class_specific_vendor_and_reward()
    {
        var db = Db();
        var ca = G("Caustic Arrow", "Metadata/Items/Gems/SkillGemPoisonArrow");
        Assert.Equal(new GemSource(1, "Enemy at the Gate", "Nessa", false), db.WhereToGet(ca, "Ranger"));
        Assert.Equal(new GemSource(1, "Enemy at the Gate", "Tarkleigh", true), db.WhereToGet(ca, "Witch"));
        Assert.Null(db.WhereToGet(ca, "Marauder"));
    }

    [Fact]
    public void Reward_beats_vendor_in_same_act()
    {
        var src = Db().WhereToGet(G("Clarity", "Metadata/Items/Gems/SkillGemClarity"), "Ranger");
        Assert.True(src!.IsReward);
        Assert.Equal("Nessa", src.Npc);
    }

    [Fact]
    public void Support_found_by_short_name_and_colour()
    {
        var db = Db();
        var gv = G("Greater Volley", support: true);
        Assert.Equal("Petarus and Vanja", db.WhereToGet(gv, "Ranger")!.Npc);
        Assert.Equal(GemColour.Green, db.Find(gv)!.Colour);
        Assert.Equal(38, db.Find(gv)!.RequiredLevel);
        Assert.Equal(1, db.Find(G("Caustic Arrow"))!.RequiredLevel);
    }

    [GemDataFact]
    public void Real_data_for_sample_build_if_cached()
    {
        var dir = Path.Combine(StateStore.Dir, "gems");
        using var g = File.OpenRead(Path.Combine(dir, "gems.json"));
        using var q = File.OpenRead(Path.Combine(dir, "quests.json"));
        var db = GemDatabase.Load(g, q);

        var build = BuildParser.Parse(PobCode.DecodeToXml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ranger-pathfinder.txt"))));
        var gems = build.Stages.Where(s => s.Title.StartsWith("Act")).SelectMany(s => s.AllGems).DistinctBy(x => x.Key).ToList();
        var missing = gems.Where(x => db.WhereToGet(x, build.ClassName) == null).Select(x => x.Name).ToList();
        Assert.True(missing.Count == 0, "no source: " + string.Join(", ", missing));
    }
}

public class StageLevelTests
{
    [Theory]
    [InlineData("lvl 12-30", 12)]
    [InlineData("Level 30", 30)]
    [InlineData("LV.45", 45)]
    [InlineData("ур. 60", 60)]
    [InlineData("12-30", 12)]
    [InlineData("70+", 70)]
    [InlineData("Leveling 1-40 (CA)", 1)]
    [InlineData("Act 3", null)]
    [InlineData("Act 7+", null)]
    [InlineData("Acts 4-5 {4}", null)]
    [InlineData("Acts 6-10 {5}", null)]
    [InlineData("Акты 6-10", null)]
    [InlineData("Early Maps", null)]
    [InlineData("Boss rush ZHP", null)]
    [InlineData("T16 maps", null)]
    public void Reads_start_level_from_title(string title, int? expected) =>
        Assert.Equal(expected, StageLevels.FromTitle(title));

    [Theory]
    [InlineData("Red Maps", 11)]
    [InlineData("Yellow maps", 6)]
    [InlineData("Early Maps", 1)]
    [InlineData("White Maps", 1)]
    [InlineData("T14 farming", 14)]
    [InlineData("tier 16", 16)]
    [InlineData("Boss rush ZHP", null)]
    [InlineData("Heis Runner", null)]
    [InlineData("Act 10", null)]
    [InlineData("Red Maps T13", 13)]
    public void Reads_map_tier_from_title(string title, int? expected) =>
        Assert.Equal(expected, StageLevels.TierFromTitle(title));

    [Theory]
    [InlineData(67, null)]
    [InlineData(68, 1)]
    [InlineData(78, 11)]
    [InlineData(83, 16)]
    [InlineData(84, 17)]
    [InlineData(86, 17)]
    public void Area_level_to_tier(int level, int? tier) =>
        Assert.Equal(tier, StageLevels.TierFromAreaLevel(level));

    [Fact]
    public void Picks_last_reached_stage()
    {
        int?[] levels = [1, 12, null, 30, 60];
        Assert.Equal(1, StageLevels.StageForLevel(levels, 12));
        Assert.Equal(0, StageLevels.StageForLevel(levels, 5));
        Assert.Equal(3, StageLevels.StageForLevel(levels, 45));
        Assert.Equal(4, StageLevels.StageForLevel(levels, 90));
        Assert.Null(StageLevels.StageForLevel([null, null], 50));
    }
}

public class BuildTests
{
    static Build Sample() =>
        BuildParser.Parse(PobCode.DecodeToXml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ranger-pathfinder.txt"))));

    [Fact]
    public void Decodes_and_pairs_specs_with_skill_sets()
    {
        var b = Sample();
        Assert.Equal("Ranger", b.ClassName);
        Assert.Equal("Pathfinder", b.Ascendancy);
        Assert.Equal("Act 1", b.Stages[0].Title);
        Assert.Contains(b.Stages, s => s.Title == "Act 10");
        Assert.All(b.Stages.Where(s => s.Title.StartsWith("Act")), s => Assert.NotNull(s.Tree));
        Assert.All(b.Stages.Where(s => s.Title.StartsWith("Act")), s => Assert.NotEmpty(s.AllGems));
    }

    [Fact]
    public void Real_world_titles_act_plus_and_typos()
    {
        var b = Sample();
        Assert.Equal(14, b.Stages.Count);
        Assert.DoesNotContain(b.Stages, s => s.Title == "Act 7+");
        var act7 = b.Stages.First(s => s.Title == "Act 7");
        Assert.Same(act7.Groups, b.Stages.First(s => s.Title == "Act 8").Groups);
        Assert.Same(act7.Groups, b.Stages.First(s => s.Title == "Act 10").Groups);
        Assert.NotSame(act7.Groups, b.Stages.First(s => s.Title == "Act 6").Groups);
        Assert.NotEmpty(b.Stages.First(s => s.Title == "Heis Runner").AllGems);
    }

    static string Xml(string specs, string sets) => $"""
        <PathOfBuilding><Build className="Witch" ascendClassName="None" level="90"/>
        <Tree>{specs}</Tree><Skills>{sets}</Skills></PathOfBuilding>
        """;

    static string Set(string title) => $"""<SkillSet title="{title}"><Skill><Gem nameSpec="Arc" skillId="Arc" level="1"/></Skill></SkillSet>""";

    [Fact]
    public void Single_tree_single_set_is_one_stage()
    {
        var b = BuildParser.Parse(Xml("""<Spec nodes="1,2" classId="3"/>""", """<Skill><Gem nameSpec="Arc" skillId="Arc"/></Skill>"""));
        var s = Assert.Single(b.Stages);
        Assert.Equal("Default", s.Title);
        Assert.NotNull(s.Tree);
        Assert.Single(s.AllGems);
    }

    [Fact]
    public void Single_tree_many_sets_gives_stage_per_set_with_that_tree()
    {
        var b = BuildParser.Parse(Xml("""<Spec title="Final" nodes="1,2" classId="3"/>""", Set("lvl 1-12") + Set("lvl 12-30") + Set("endgame")));
        Assert.Equal(["lvl 1-12", "lvl 12-30", "endgame"], b.Stages.Select(s => s.Title));
        Assert.All(b.Stages, s => Assert.Equal("Final", s.Tree!.Title));
    }

    [Fact]
    public void Free_form_titles_are_kept_in_tree_order()
    {
        var b = BuildParser.Parse(Xml(
            """<Spec title="Leveling" nodes="1"/><Spec title="Mapping" nodes="1,2"/><Spec title="Endgame" nodes="1,2,3"/>""",
            Set("Leveling") + Set("Endgame")));
        Assert.Equal(["Leveling", "Mapping", "Endgame"], b.Stages.Select(s => s.Title));
        Assert.Same(b.Stages[0].Groups, b.Stages[1].Groups);
    }

    [Fact]
    public void Swap_pairs_gems_inside_a_link()
    {
        var b = Sample();
        var lines = GemSwap.Diff(b.Stages[2], b.Stages[3]);   // Act 3 -> Act 4
        string Show(SwapLine l) => $"{l.Old?.Name ?? "+"} -> {l.New?.Name ?? "x"}";
        var shown = lines.Select(Show).ToList();
        Assert.Contains("Volley -> Greater Volley", shown);
        Assert.Contains("Void Manipulation -> Greater Multiple Projectiles", shown);
        // nothing that only moved, nothing that stays
        Assert.DoesNotContain(lines, l => l.Old?.Name == "Poisonous Concoction" || l.New?.Name == "Poisonous Concoction");
        Assert.Empty(GemSwap.Diff(b.Stages[7], b.Stages[8]));   // Act 8 -> Act 9 share "Act 7+"
    }

    [Theory]
    [InlineData("[Poison|Poisons] you inflict deal Damage 20% [DamageFaster|Faster]", "Poisons you inflict deal Damage 20% Faster")]
    [InlineData("Enemies you [Blind] are [Withered]", "Enemies you Blind are Withered")]
    [InlineData("Grants Level 25 [Envy::25|Envy] Skill", "Grants Level 25 Envy Skill")]
    [InlineData("10% increased Flask Charges gained", "10% increased Flask Charges gained")]
    public void Tree_text_link_markup_is_shown_as_the_game_shows_it(string raw, string shown) =>
        Assert.Equal(shown, PassiveTree.PlainText(raw));

    [Fact]
    public void Bandit_and_pantheon_come_from_the_build()
    {
        var b = Sample();
        Assert.Equal("помочь Alira — убить Kraityn и Oak", BuildChoices.BanditText(b.Bandit));
        Assert.Equal("The Brine King + Ryslatha", BuildChoices.PantheonText(b.PantheonMajor, b.PantheonMinor));
        Assert.Equal("убить всех троих (+2 пассивки)", BuildChoices.BanditText("None"));
        Assert.Null(BuildChoices.PantheonText("None", ""));
    }

    [Fact]
    public void Gem_switched_off_in_pob_is_not_on_the_stage()
    {
        var b = Sample();
        // Haste sits next to Grace switched off until "Act 7+"
        Assert.DoesNotContain(b.Stages[5].AllGems, g => g.Name == "Haste");   // Act 6
        Assert.Contains(b.Stages[6].AllGems, g => g.Name == "Haste");         // Act 7
        Assert.Contains(GemSwap.Diff(b.Stages[5], b.Stages[6]), l => l.Old == null && l.New?.Name == "Haste");
    }

    [Fact]
    public void Act1_has_section_headers_without_separator_lines()
    {
        var act1 = Sample().Stages[0];
        Assert.Contains(act1.Groups, g => g.IsHeader && g.Label == "1-12");
        Assert.DoesNotContain(act1.Groups, g => g.Label.StartsWith("---"));
        var caustic = act1.AllGems.First(g => g.Name == "Caustic Arrow");
        Assert.False(caustic.IsSupport);
        Assert.True(act1.AllGems.First(g => g.Name == "Volley").IsSupport);
    }

    [Fact]
    public void Build_id_is_stable()
    {
        Assert.Equal(Sample().Id, Sample().Id);
    }

    [Theory]
    [InlineData("https://pobb.in/P7nwK5UeLdIa", "https://pobb.in/P7nwK5UeLdIa/raw")]
    [InlineData("https://pobb.in/P7nwK5UeLdIa/raw", "https://pobb.in/P7nwK5UeLdIa/raw")]
    [InlineData("https://pastebin.com/abc123", "https://pastebin.com/raw/abc123")]
    public void Raw_urls(string input, string expected) =>
        Assert.Equal(expected, PobCode.ToRawUrl(new Uri(input)).ToString());

    [Fact]
    public void Garbage_code_gives_readable_error() =>
        Assert.Throws<PobFormatException>(() => PobCode.DecodeToXml("not a pob code!!"));
}

public class LogTests
{
    [Fact]
    public void Parses_area_zone_and_level()
    {
        var a = Assert.IsType<AreaGenerated>(ClientLogParser.ParseLine(
            "2026/11/14 12:00:01 123456 cff945b9 [DEBUG Client 1234] Generating level 23 area \"1_3_town\" with seed 1"));
        Assert.Equal(3, a.Act);
        Assert.True(a.IsTown);

        Assert.Equal(6, Assert.IsType<AreaGenerated>(ClientLogParser.ParseLine("... Generating level 33 area \"2_6_1\" with seed 9")).Act);
        Assert.True(Assert.IsType<AreaGenerated>(ClientLogParser.ParseLine("... Generating level 68 area \"MapWorldsStrand\" with seed 9")).IsMap);

        var z = Assert.IsType<ZoneEntered>(ClientLogParser.ParseLine("2026/11/14 12:00:02 1 a [INFO Client 1234] : You have entered The Sarn Encampment."));
        Assert.Equal("The Sarn Encampment", z.Name);

        var l = Assert.IsType<LevelUp>(ClientLogParser.ParseLine("2026/11/14 12:00:03 1 a [INFO Client 1234] : MyRanger (Ranger) is now level 12"));
        Assert.Equal(("MyRanger", 12), (l.Character, l.Level));

        Assert.Null(ClientLogParser.ParseLine("2026/11/14 12:00:03 1 a [INFO Client 1234] @From Someone: hi"));
    }
}

public class PlannerTests
{
    // start(0) - 1 - 2 - N3(notable) ; 1 - 4 - K5(keystone, far branch)
    const string Json = """
    {
      "constants": { "skillsPerOrbit": [1,6,16,16,40,72,72], "orbitRadii": [0,82,162,335,493,662,846] },
      "groups": { "1": {"x":0,"y":0}, "2": {"x":1000,"y":0}, "3": {"x":2000,"y":0}, "4": {"x":1000,"y":1000}, "5": {"x":1000,"y":2000}, "6": {"x":1000,"y":3000} },
      "nodes": {
        "root": { "out": [] },
        "10": { "name": "Start", "classStartIndex": 2, "group": 1, "orbit": 0, "orbitIndex": 0, "out": ["11"], "in": [] },
        "11": { "name": "a", "group": 2, "orbit": 0, "orbitIndex": 0, "out": ["12","14"], "in": ["10"] },
        "12": { "name": "b", "group": 3, "orbit": 0, "orbitIndex": 0, "out": ["13"], "in": ["11"] },
        "13": { "name": "Notable", "isNotable": true, "group": 3, "orbit": 1, "orbitIndex": 0, "out": [], "in": ["12"] },
        "14": { "name": "c", "group": 4, "orbit": 0, "orbitIndex": 0, "out": ["15"], "in": ["11"] },
        "15": { "name": "d", "group": 5, "orbit": 0, "orbitIndex": 0, "out": ["16"], "in": ["14"] },
        "16": { "name": "Keystone", "isKeystone": true, "group": 6, "orbit": 0, "orbitIndex": 0, "out": [], "in": ["15"] },
        "20": { "name": "Asc", "ascendancyName": "Pathfinder", "isNotable": true, "group": 1, "orbit": 0, "orbitIndex": 0, "out": [], "in": [] }
      }
    }
    """;

    static PassiveTree Tree() => PassiveTree.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json)));

    [Fact]
    public void Takes_nearest_key_node_first_then_the_rest()
    {
        var plan = PathPlanner.Plan(Tree(), [], [11, 12, 13, 14, 15, 16, 20], classId: 2);
        Assert.Equal([11, 12, 13, 14, 15, 16], plan.Order);
        Assert.Equal([20], plan.Ascendancy);
    }

    [Fact]
    public void Already_taken_nodes_are_skipped_and_extras_refunded()
    {
        var plan = PathPlanner.Plan(Tree(), [11, 12, 14], [11, 12, 13], classId: 2);
        Assert.Equal([13], plan.Order);
        Assert.Equal(new HashSet<int> { 14 }, plan.Refund.ToHashSet());
    }

    [Fact]
    public void Positions_and_edges()
    {
        var t = Tree();
        Assert.Equal(2000, t.Nodes[13].X, 3);
        Assert.Equal(-82, t.Nodes[13].Y, 3);
        Assert.False(t.Nodes[20].Drawable);
        Assert.Contains(t.Edges, e => (e.A, e.B) is (12, 13) or (13, 12));
    }
}

public class RealTreeTests
{
    [TreeFact]
    public void Loads_cached_ggg_tree_if_present()
    {
        using var fs = File.OpenRead(TreeRepository.CachePath);
        var t = PassiveTree.Load(fs);
        Assert.True(t.Nodes.Count > 2000);
        Assert.NotNull(t.ClassStart(2));
        Assert.True(t.Edges.Count > 2000);
    }
}
