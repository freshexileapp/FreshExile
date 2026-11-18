using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class VendorSearchTests
{
    static readonly string[] All =
    [
        "Volley Support", "Greater Volley Support", "Herald of Ice", "Herald of Ash", "Onslaught Support",
        "Caustic Arrow", "Toxic Rain", "Rain of Arrows", "Frenzy", "Vaal Rain of Arrows", "Added Cold Damage Support",
    ];

    [Fact]
    public void Picks_the_shortest_whole_word_no_other_gem_has()
    {
        Assert.Equal("ice", VendorSearch.Piece("Herald of Ice", All));
        Assert.Equal("frenzy", VendorSearch.Piece("Frenzy", All));
        Assert.Equal("greater", VendorSearch.Piece("Greater Volley Support", All));
    }

    [Fact]
    public void Vaal_versions_do_not_count_they_are_not_sold() =>
        Assert.Equal("arrows", VendorSearch.Piece("Rain of Arrows", All));

    [Fact]
    public void Two_words_when_no_single_word_is_unique()
    {
        string[] all = [.. All, "Ice Nova"];
        Assert.Equal("of ice", VendorSearch.Piece("Herald of Ice", all));
    }

    [Fact]
    public void Name_inside_another_gem_falls_back_to_the_name_without_support() =>
        Assert.Equal("volley", VendorSearch.Piece("Volley Support", All));

    [Fact]
    public void Joins_with_bar_and_quotes_only_when_there_is_a_space()
    {
        Assert.Equal(["ice|frenzy"], VendorSearch.Build(["Herald of Ice", "Frenzy"], All));
        string[] all = [.. All, "Ice Nova"];
        Assert.Equal(["\"of ice|frenzy\""], VendorSearch.Build(["Herald of Ice", "Frenzy"], all));
    }

    [Fact]
    public void Splits_into_strings_of_at_most_50_chars()
    {
        var names = Enumerable.Range(0, 12).Select(i => $"Gem Number {i:D2} Long").ToList();
        var parts = VendorSearch.Build(names, names);
        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.InRange(p.Length, 1, VendorSearch.MaxLength));
        Assert.Equal(12, parts.Sum(p => p.Trim('"').Split('|').Length));
    }

    [Fact]
    public void Empty_list_gives_nothing() => Assert.Empty(VendorSearch.Build([], All));

    [GemDataFact]
    public void Real_gem_names_give_short_unique_pieces()
    {
        var dir = Path.Combine(StateStore.Dir, "gems");
        using var g = File.OpenRead(Path.Combine(dir, "gems.json"));
        using var q = File.OpenRead(Path.Combine(dir, "quests.json"));
        var names = GemDatabase.Load(g, q).Names.ToList();
        var wanted = new[] { "Caustic Arrow Support", "Herald of Ice", "Onslaught Support", "Frenzy", "Toxic Rain", "Vitality" }
            .Where(n => names.Contains(n)).ToList();
        var parts = VendorSearch.Build(wanted, names);
        Assert.All(parts, p => Assert.InRange(p.Length, 1, VendorSearch.MaxLength));
        Assert.Equal(wanted.Count, parts.Sum(p => p.Trim('"').Split('|').Length));
    }
}

public class NewCharacterTests
{
    [Fact]
    public void Level_up_under_another_name_is_a_new_character() =>
        Assert.Equal("AltRanger", NewCharacter.Detect(new LevelUp("AltRanger", "Ranger", 2), "TestRanger", 5));

    [Fact]
    public void Same_name_any_case_is_not() =>
        Assert.Null(NewCharacter.Detect(new LevelUp("testranger", "Ranger", 39), "TestRanger", 5));

    [Fact]
    public void Unknown_character_is_filled_in_without_asking() =>
        Assert.Null(NewCharacter.Detect(new LevelUp("TestRanger", "Ranger", 2), "", 0));

    [Fact]
    public void Twilight_strand_past_the_first_stage_is_a_new_character()
    {
        Assert.Equal("", NewCharacter.Detect(new AreaGenerated("1_1_1", 1), "TestRanger", 3));
        Assert.Null(NewCharacter.Detect(new AreaGenerated("1_1_1", 1), "TestRanger", 0));
        Assert.Null(NewCharacter.Detect(new AreaGenerated("1_1_town", 13), "TestRanger", 3));
    }

    [Fact]
    public void Twilight_strand_means_level_1_whoever_levelled_last_in_the_log()
    {
        var f = new StageFollower([]);
        f.OnEvent(new LevelUp("OldRanger", "Pathfinder", 87), 0, follow: false);
        Assert.Equal(87, f.Level);
        f.OnEvent(new AreaGenerated("1_1_1", 1), 0, follow: false);
        Assert.Equal(1, f.Level);
        Assert.Null(f.Character);
    }

    [Fact]
    public void Twilight_strand_on_the_first_stage_asks_when_the_old_character_left_marks_behind()
    {
        Assert.Equal("", NewCharacter.Detect(new AreaGenerated("1_1_1", 1), "TestRanger", 0, hasProgress: true));
        Assert.Null(NewCharacter.Detect(new AreaGenerated("1_1_1", 1), "TestRanger", 0, hasProgress: false));
    }
}

/// <summary>Real frames: the inventory header is found where the inventory is open and nowhere else.</summary>
public class InventoryPanelTests
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestData", "frames");

    static (byte[] Bytes, int W, int H) Pixels(string file)
    {
        using var bmp = new System.Drawing.Bitmap(Path.Combine(Dir, file));
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * bmp.Height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);
        return (bytes, bmp.Width, bmp.Height);
    }

    public static IEnumerable<object[]> Frames() =>
        Directory.GetFiles(Dir, "*.png").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Frames))]
    public void Inventory_is_seen_only_where_it_is_open(string file)
    {
        var (b, w, h) = Pixels(file);
        var expected = file.Contains("inventory") || file.Contains("vendor");
        var diff = InventoryPanel.Diff(InventoryPanel.Patch(b, w, h, w * 4));
        Assert.True(expected == diff < InventoryPanel.MaxDiff, $"{file}: diff {diff:F1}, expected open={expected}");
    }

    [Fact]
    public void Panel_starts_at_the_same_share_of_the_height_on_any_screen()
    {
        Assert.Equal(1256, InventoryPanel.PanelLeft(1920, 1080));
        Assert.Equal(2560 - 886, InventoryPanel.PanelLeft(2560, 1440));
    }
}

public class RunTimerTests
{
    static readonly DateTime T0 = new(2026, 11, 15, 1, 0, 0);
    static AreaGenerated Area(string id, long seed = 1) => new(id, 10) { Seed = seed };

    [Fact]
    public void Parser_keeps_seed_and_line_time()
    {
        var a = Assert.IsType<AreaGenerated>(ClientLogParser.ParseLine(
            "2026/11/15 01:16:59 123 cff [DEBUG Client 1] Generating level 68 area \"MapWorldsStrand\" with seed 4242"));
        Assert.Equal(4242, a.Seed);
        Assert.Equal(new DateTime(2026, 11, 15, 1, 16, 59), a.At);
    }

    [Fact]
    public void Act_time_runs_through_its_town_and_restarts_in_the_next_act()
    {
        var t = new RunTimer();
        t.OnArea(Area("1_4_1"), T0);
        t.OnArea(Area("1_4_town"), T0.AddMinutes(5));
        Assert.Equal("акт 9:30", t.Text(T0.AddMinutes(9.5)));
        t.OnArea(Area("1_5_1"), T0.AddMinutes(20));
        Assert.Equal("акт 1:00", t.Text(T0.AddMinutes(21)));
    }

    [Fact]
    public void Trip_back_to_an_old_town_counts_to_the_current_act()
    {
        var t = new RunTimer();
        t.OnArea(Area("1_4_1"), T0);
        t.OnArea(Area("1_3_town"), T0.AddMinutes(10));
        t.OnArea(Area("1_4_2"), T0.AddMinutes(12));
        Assert.Equal("акт 15:00", t.Text(T0.AddMinutes(15)));
    }

    [Fact]
    public void Twilight_strand_starts_a_new_run_from_act_one()
    {
        var t = new RunTimer();
        t.OnArea(Area("1_4_1"), T0);
        t.OnArea(Area("1_1_1"), T0.AddMinutes(30));
        Assert.Equal("акт 2:00", t.Text(T0.AddMinutes(32)));
        t.OnArea(Area("1_2_1"), T0.AddMinutes(40));
        Assert.Equal("акт 1:00", t.Text(T0.AddMinutes(41)));
    }

    [Fact]
    public void Map_time_freezes_in_the_hideout_and_runs_on_in_the_same_map()
    {
        var t = new RunTimer();
        t.OnArea(Area("MapWorldsStrand", 7), T0);
        t.OnArea(Area("HideoutFelled"), T0.AddMinutes(3));
        Assert.Equal("карта 3:00", t.Text(T0.AddMinutes(10)));
        Assert.True(t.Stopped);
        t.OnArea(Area("MapWorldsStrand", 7), T0.AddMinutes(11));
        Assert.Equal("карта 12:00", t.Text(T0.AddMinutes(12)));
        t.OnArea(Area("MapWorldsBeach", 8), T0.AddMinutes(13));
        Assert.Equal("карта 0:30", t.Text(T0.AddMinutes(13.5)));
    }

    [Fact]
    public void Hours_are_shown_past_sixty_minutes() =>
        Assert.Equal("1:02:03", RunTimer.Format(new TimeSpan(1, 2, 3)));
}

/// <summary>"ЛАБА · …": ascendancy notables a stage adds over the previous one, from the real Ranger build.</summary>
public class LabTests
{
    [TreeFact]
    public void Every_ascendancy_notable_is_announced_on_the_stage_where_it_appears()
    {
        using var fs = File.OpenRead(TreeRepository.CachePath);
        var tree = PassiveTree.Load(fs);
        var b = BuildParser.Parse(PobCode.DecodeToXml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ranger-pathfinder.txt"))));
        bool AscNotable(int id) => tree.Nodes.TryGetValue(id, out var n) && n is { Ascendancy: not null, Kind: NodeKind.Notable };

        IReadOnlyList<int> prev = [];
        var seen = new HashSet<int>();
        foreach (var stage in b.Stages)
        {
            if (stage.Tree is not { } t) continue;
            var announced = PathPlanner.Plan(tree, prev, t.Nodes, t.ClassId).Ascendancy.Where(AscNotable).ToHashSet();
            // exactly the notables this stage has and the previous one did not
            var added = t.Nodes.Where(AscNotable).Where(id => !prev.Contains(id)).ToHashSet();
            Assert.True(added.SetEquals(announced), $"{stage.Title}: {string.Join(", ", announced.Select(id => tree.Nodes[id].Name))}");
            seen.UnionWith(announced);
            prev = t.Nodes;
        }
        // this build respecs its ascendancy late (Master Surgeon → Nature's Boon): both sets are announced
        Assert.True(seen.Count >= 5);
    }
}
