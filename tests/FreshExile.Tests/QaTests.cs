using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

/// <summary>
/// Real screenshots from the game (TestData/frames, expectations in frames.json, checked by eye when added).
/// "open-*" frames must be found at their scale and offset, "closed-*" frames must never open the overlay.
/// New frames: drop a PNG with that prefix, run the generator, review the preview, update frames.json.
/// </summary>
public class FrameRegressionTests
{
    sealed record FrameCase(string file, bool open, double? scale, double? offsetX, double? offsetY);
    sealed record Manifest(List<int> allocated, string titleFrame, List<FrameCase> frames);

    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestData", "frames");

    static Manifest Load() =>
        JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(Dir, "frames.json")))!;

    static (byte[] Bytes, int W, int H) Pixels(string file)
    {
        using var bmp = new Bitmap(Path.Combine(Dir, file));
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * bmp.Height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);
        return (bytes, bmp.Width, bmp.Height);
    }

    static PassiveTree Tree()
    {
        using var fs = File.OpenRead(TreeRepository.CachePath);
        return PassiveTree.Load(fs);
    }

    public static IEnumerable<object[]> Cases() => Load().frames.Select(f => new object[] { f.file });

    [TreeTheory]
    [MemberData(nameof(Cases))]
    public void Frame_verdict_matches_the_reviewed_expectation(string file)
    {
        var tree = Tree();
        var m = Load();
        var expected = m.frames.Single(f => f.file == file);
        var aligner = new TreeAligner(TreeAligner.BuildTemplate(tree, m.allocated));
        var title = Pixels(m.titleFrame);
        var patch = TreeAligner.HeaderPatch(title.Bytes, title.W, title.H, title.W * 4);
        var (bytes, w, h) = Pixels(file);

        // from scratch: nothing calibrated yet, so the sweep has to find the zoom on its own
        var v = TreeDetector.Evaluate(aligner, bytes, w, h, [], patch, open: false, allowSweep: true);
        Assert.Equal(expected.open, v.Good);
        if (!expected.open) return;

        var a = v.Align!.Value;
        Assert.InRange(a.Scale, expected.scale!.Value * 0.98, expected.scale.Value * 1.02);
        Assert.InRange(a.OffsetX, expected.offsetX!.Value - 8, expected.offsetX.Value + 8);
        Assert.InRange(a.OffsetY, expected.offsetY!.Value - 8, expected.offsetY.Value + 8);

        // and tracking with the remembered zooms lands on the same spot
        var known = m.frames.Where(f => f.open).Select(f => f.scale!.Value).ToList();
        var t = TreeDetector.Evaluate(aligner, bytes, w, h, known, patch, open: true, allowSweep: false);
        Assert.True(t.Good);
        Assert.InRange(t.Align!.Value.OffsetX, expected.offsetX.Value - 8, expected.offsetX.Value + 8);
    }

    // a fresh Ranger, nothing allocated (chat painted over): the tree is found by its own drawing around the class start
    const string Fresh = "open-fresh-ranger.png";
    const int Ranger = 2;

    [TreeFact]
    public void A_fresh_character_is_found_by_the_trees_drawing_around_its_class_start()
    {
        var tree = Tree();
        var aligner = new TreeAligner(TreeAligner.BuildStructureTemplate(tree, tree.ClassStart(Ranger)!.Id), structure: true);
        var (bytes, w, h) = Pixels(Fresh);

        // from scratch (the sweep) and with the zooms remembered, the start lands where the game draws it
        foreach (var known in new List<double>[] { [], [.. Load().frames.Where(f => f.open).Select(f => f.scale!.Value), 0.1079] })
        {
            var v = TreeDetector.Evaluate(aligner, bytes, w, h, known, null, open: false, allowSweep: true);
            Assert.True(v.Good);
            var a = v.Align!.Value;
            Assert.InRange(a.Scale, 0.1079 * 0.98, 0.1079 * 1.02);
            var start = tree.ClassStart(Ranger)!;
            Assert.InRange(start.X * a.Scale + a.OffsetX, 750 - 8, 750 + 8);
            Assert.InRange(start.Y * a.Scale + a.OffsetY, 560 - 8, 560 + 8);
        }
    }

    [TreeFact]
    public void A_fresh_character_is_found_on_a_closer_zoom_too()
    {
        // zoomed in (0.174): thicker links and icons score lower than on the far zoom, still above any game frame
        var tree = Tree();
        var start = tree.ClassStart(Ranger)!;
        var aligner = new TreeAligner(TreeAligner.BuildStructureTemplate(tree, start.Id), structure: true);
        var (bytes, w, h) = Pixels("open-fresh-ranger-zoom.png");
        // the far zoom is remembered and misses; the title is on screen, so the sweep finds the new zoom
        var title = TreeAligner.HeaderPatch(bytes, w, h, w * 4);
        var v = TreeDetector.Evaluate(aligner, bytes, w, h, [0.108], title, open: false, allowSweep: true);
        Assert.True(v.Good);
        var a = v.Align!.Value;
        Assert.InRange(a.Scale, 0.174 * 0.98, 0.174 * 1.02);
        Assert.InRange(start.X * a.Scale + a.OffsetX, 605 - 10, 605 + 10);
        Assert.InRange(start.Y * a.Scale + a.OffsetY, 483 - 10, 483 + 10);
    }

    [Fact]
    public void The_games_zoom_steps_are_tried_with_the_remembered_ones_and_scale_with_the_screen()
    {
        // nothing remembered (a recalibration cleared it): the game's steps are still there, 0.244 among them
        var at1080 = TreeDetector.ZoomsToTry([], 1080);
        Assert.Contains(at1080, z => Math.Abs(z - 0.244) < 0.001);
        // a remembered zoom comes first; a near-duplicate of a game step is not tried twice
        var mixed = TreeDetector.ZoomsToTry([0.1079], 1080);
        Assert.Equal(0.1079, mixed[0]);
        Assert.Equal(at1080.Count, mixed.Count);
        // 1440 px of height: every step a third larger
        Assert.Contains(TreeDetector.ZoomsToTry([], 1440), z => Math.Abs(z - 0.244 * 1440 / 1080) < 0.001);
    }

    [TreeFact]
    public void Closed_frames_never_look_like_a_fresh_characters_tree()
    {
        var tree = Tree();
        var aligner = new TreeAligner(TreeAligner.BuildStructureTemplate(tree, tree.ClassStart(Ranger)!.Id), structure: true);
        // every zoom the app tries (the frames' ones and the game's steps); no title check: the structure score alone must say no
        var known = TreeDetector.ZoomsToTry(Load().frames.Where(f => f.open).Select(f => f.scale!.Value), 1080);
        foreach (var f in Load().frames.Where(f => !f.open))
        {
            var (bytes, w, h) = Pixels(f.file);
            Assert.False(TreeDetector.Evaluate(aligner, bytes, w, h, known, null, open: false, allowSweep: true).Good, f.file);
        }
    }

    [TreeFact]
    public void Closed_frames_stay_closed_even_with_every_zoom_known()
    {
        var tree = Tree();
        var m = Load();
        var aligner = new TreeAligner(TreeAligner.BuildTemplate(tree, m.allocated));
        var title = Pixels(m.titleFrame);
        var patch = TreeAligner.HeaderPatch(title.Bytes, title.W, title.H, title.W * 4);
        var known = m.frames.Where(f => f.open).Select(f => f.scale!.Value).ToList();

        foreach (var f in m.frames.Where(f => !f.open))
        {
            var (bytes, w, h) = Pixels(f.file);
            var v = TreeDetector.Evaluate(aligner, bytes, w, h, known, patch, open: false, allowSweep: true);
            Assert.False(v.Good, f.file);
            Assert.False(v.TitleSeen, f.file);
        }
    }
}

/// <summary>Real Client.txt of a Ranger from the first zone of Act 1 to Act 4, fed through the follower.</summary>
public class LogReplayTests
{
    static Build Ranger() =>
        BuildParser.Parse(PobCode.DecodeToXml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ranger-pathfinder.txt"))));

    static StageFollower Follower(Build b) => new(b.Stages.Select(s => s.Title).ToList())
    {
        StartLevels = b.Stages.Select(s => StageLevels.FromTitle(s.Title)).ToList(),
        StartTiers = b.Stages.Select(s => StageLevels.TierFromTitle(s.Title)).ToList(),
    };

    [Fact]
    public void Acts_one_to_four_switch_forward_in_order_and_town_follows_the_area()
    {
        var b = Ranger();
        var f = Follower(b);
        var stage = 0;
        var visited = new List<string>();

        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestData", "client-ranger-acts1-4.txt")))
        {
            var e = ClientLogParser.ParseLine(line);
            if (e == null) continue;
            if (f.OnEvent(e, stage, follow: true) is { } sw)
            {
                Assert.True(sw.Stage > stage, $"went back to {b.Stages[sw.Stage].Title}");
                stage = sw.Stage;
                visited.Add(b.Stages[stage].Title);
            }
            if (e is AreaGenerated a)
                Assert.Equal(a.IsTown || a.IsHideout, f.InTown);
        }

        Assert.Equal(["Act 2", "Act 3", "Act 4"], visited);
        Assert.Equal(4, f.Act);
        Assert.Equal(38, f.Level);
        Assert.Equal("TestRanger", f.Character);
    }

    [Fact]
    public void Same_log_with_follow_off_never_switches()
    {
        var f = Follower(Ranger());
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestData", "client-ranger-acts1-4.txt")))
            if (ClientLogParser.ParseLine(line) is { } e)
                Assert.Null(f.OnEvent(e, 0, follow: false));
    }

    static LogEvent Area(int level, string id) => ClientLogParser.ParseLine($"2026/11/14 12:00:00 1 a [DEBUG Client 1] Generating level {level} area \"{id}\" with seed 1")!;

    [Fact]
    public void Maps_first_map_then_red_tier_and_old_towns_do_not_pull_back()
    {
        var b = Ranger();
        var f = Follower(b);
        var act10 = b.Stages.ToList().FindIndex(s => s.Title == "Act 10");
        var early = b.Stages.ToList().FindIndex(s => s.Title == "Early Maps");
        var red = b.Stages.ToList().FindIndex(s => s.Title == "Red Maps");

        Assert.Equal(early, f.OnEvent(Area(68, "MapWorldsLookout"), act10, true)!.Stage);
        Assert.Null(f.OnEvent(Area(73, "MapWorldsCrater"), early, true));        // yellow: no stage for T6
        var sw = f.OnEvent(Area(79, "MapWorldsDunes"), early, true)!;
        Assert.Equal(red, sw.Stage);
        Assert.StartsWith("T12", sw.Reason);
        Assert.Null(f.OnEvent(Area(68, "2_10_town"), red, true));                // back in Oriath: stay
        Assert.Null(f.OnEvent(Area(68, "MapWorldsLookout"), red, true));         // a white map later: stay
    }

    [Fact]
    public void Level_titles_switch_by_character_level_only_forward()
    {
        var f = new StageFollower(["lvl 1-12", "lvl 12-30", "lvl 30+"])
        {
            StartLevels = [1, 12, 30],
            StartTiers = [null, null, null],
        };
        LogEvent Lvl(int n) => new LevelUp("Hero", "Witch", n);

        Assert.Null(f.OnEvent(Lvl(11), 0, true));
        Assert.Equal(1, f.OnEvent(Lvl(12), 0, true)!.Stage);
        Assert.Equal(2, f.OnEvent(Lvl(31), 1, true)!.Stage);
        Assert.Null(f.OnEvent(Lvl(5), 2, true));
    }

    [Fact]
    public void Hideout_is_town_but_does_not_move_the_guide()
    {
        var f = Follower(Ranger());
        Assert.Null(f.OnEvent(Area(40, "HideoutFelled"), 0, true));
        Assert.True(f.InTown);
        f.OnEvent(Area(40, "1_4_2"), 3, true);
        Assert.False(f.InTown);
    }
}
