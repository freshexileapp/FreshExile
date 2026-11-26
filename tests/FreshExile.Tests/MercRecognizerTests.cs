using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using FreshExile.Core;
using Xunit;
using Xunit.Abstractions;

namespace FreshExile.Tests;

public class MercRecognizerTests(ITestOutputHelper output)
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestData", "merc");

    internal static Pixels Load(string path)
    {
        using var bmp = new Bitmap(path);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[bmp.Width * bmp.Height * 4];
        for (var y = 0; y < bmp.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * bmp.Width * 4, bmp.Width * 4);
        bmp.UnlockBits(data);
        return new Pixels(bytes, bmp.Width, bmp.Height);
    }

    static List<OcrLine> Ocr(string file) => File.ReadAllLines(Path.Combine(Dir, file))
        .Select(l => l.Split('\t'))
        .Select(p => new OcrLine(p[4], double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture),
            double.Parse(p[2], CultureInfo.InvariantCulture), double.Parse(p[3], CultureInfo.InvariantCulture)))
        .ToList();

    static Pixels? Icon(string path)
    {
        var f = Path.Combine(Dir, "icons", path.Replace('/', '_') + ".png");
        return File.Exists(f) ? Load(f) : null;
    }

    /// <summary>Each found slot as it is on the screen next to the icon it was taken for, 4x: to look at by eye.</summary>
    static void Debug(MercReading r, string png, string file = "zob-shock-ambusher.png")
    {
        using var shot = new Bitmap(Path.Combine(Dir, file));
        using var outImg = new Bitmap(2 * 4 * 40 + 20, Math.Max(1, r.Slots.Count) * 4 * 40);
        using var g = Graphics.FromImage(outImg);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.Clear(Color.Black);
        for (var i = 0; i < r.Slots.Count; i++)
        {
            var sl = r.Slots[i];
            g.DrawImage(shot, new Rectangle(0, i * 160, sl.Size * 4, sl.Size * 4), new Rectangle(sl.X, sl.Y, sl.Size, sl.Size), GraphicsUnit.Pixel);
            var f = Path.Combine(Dir, "icons", sl.Icon.Replace('/', '_') + ".png");
            using var ic = new Bitmap(f);
            g.DrawImage(ic, new Rectangle(180, i * 160, sl.Size * 4, sl.Size * 4));
            g.DrawString($"T{sl.Tier} {sl.Score:0.00}", SystemFonts.DefaultFont, Brushes.White, 300, i * 160 + 5);
        }
        outImg.Save(png);
    }

    [Fact]
    public void A_dim_middle_stroke_still_makes_three()
    {
        // Lightning Trap's first support: "III" whose middle stroke is AF9362, not the outer strokes' FED58B
        var shot = Load(Path.Combine(Dir, "zob-shock-ambusher.png"));
        Assert.Equal(3, MercRecognizer.Tier(shot, 360, 626, 31, output.WriteLine));
    }

    [Fact]
    public void The_mercenary_window_reads_as_its_build_skills_and_supports()
    {
        var r = MercRecognizer.Read(Load(Path.Combine(Dir, "zob-shock-ambusher.png")), Ocr("zob-ocr.tsv"), MercData.Default, Icon);
        foreach (var s in r.Warrant.Skills)
            output.WriteLine($"{s.Name}: {string.Join(", ", s.Supports.Select(x => x.TradeText))}");
        foreach (var n in r.Notes) output.WriteLine("note: " + n);
        foreach (var sl in r.Slots) output.WriteLine($"slot {sl.Skill} x={sl.X} y={sl.Y} s={sl.Size} score={sl.Score:0.00} T{sl.Tier} {sl.Icon}");

        if (Environment.GetEnvironmentVariable("MERC_DEBUG") is { Length: > 0 } dbg) Debug(r, dbg);
        Assert.Equal("Infamous Shock Ambusher", r.Warrant.Build);
        Assert.Equal(83, r.Warrant.Level);
        Assert.Equal("Zob, the Azadin Howler", r.Warrant.Mercenary);
        Assert.Equal(["Conductivity", "Vaal Lightning Trap", "Lightning Trap", "Lightning Spire Trap", "Zealotry", "Summon Skitterbots"],
            r.Warrant.Skills.Select(s => s.Name));
        // the window: 2, 2, 5, 5, 0, 0 icons
        Assert.Equal([2, 2, 5, 5, 0, 0], r.Warrant.Skills.Select(s => s.Supports.Count));
        // the numerals as the window draws them
        Assert.Equal([2, 3, 2, 3, 2, 2, 2, 3, 2, 2, 2, 2, 2], r.Warrant.Skills.SelectMany(s => s.Supports).Skip(1).Select(x => x.Tier));
        // icons told apart among the skill's candidates
        Assert.Equal(["More Duration", "Lightning Penetration", "Trap and Mine Damage", "Added Lightning", "Multiple Traps"],
            new[] { r.Warrant.Skills[0].Supports[1] }.Concat(r.Warrant.Skills[2].Supports.Skip(1)).Select(x => x.Name.Replace("Greater ", "").Replace("Lesser ", "")));
    }

    [Fact]
    public void A_whole_screen_capture_cut_to_the_window_reads_too()
    {
        // a 1920x1080 screenshot, the mercenary window circled by hand: a Bladecaster, smaller in its frame
        var r = MercRecognizer.Read(Load(Path.Combine(Dir, "forkan-bladecaster.png")), Ocr("forkan-ocr.tsv"), MercData.Default, Icon);
        foreach (var s in r.Warrant.Skills)
            output.WriteLine($"{s.Name}: {string.Join(", ", s.Supports.Select(x => x.TradeText))}");
        foreach (var sl in r.Slots) output.WriteLine($"slot {sl.Skill} x={sl.X} y={sl.Y} s={sl.Size} score={sl.Score:0.00} T{sl.Tier} {sl.Icon}");
        if (Environment.GetEnvironmentVariable("MERC_DEBUG2") is { Length: > 0 } dbg) Debug(r, dbg, "forkan-bladecaster.png");

        Assert.Equal("Bladecaster", r.Warrant.Build);
        Assert.Equal(83, r.Warrant.Level);
        Assert.Equal("Forkan, the Fraudster", r.Warrant.Mercenary);
        Assert.Equal(["Clutches of the Damned", "Bloody Warp", "Bladefall of Trarthus", "Ethereal Knives", "Dash", "Aspect of the Spider"],
            r.Warrant.Skills.Select(s => s.Name));
        Assert.Equal([0, 2, 4, 4, 0, 0], r.Warrant.Skills.Select(s => s.Supports.Count));
        Assert.Equal([3, 2, 2, 2, 3, 2, 2, 2, 2, 2], r.Warrant.Skills.SelectMany(s => s.Supports).Select(x => x.Tier));
        Assert.Equal(["Faster Casting", "Critical Damage", "Pierce"], r.Warrant.Skills[3].Supports.Skip(1).Select(x => x.Name));
        // the crit chance wheel and the crit damage one (the same wheel with arrows) side by side
        Assert.Equal(["Increased Area of Effect", "Critical Chance", "Greater Critical Damage"], r.Warrant.Skills[2].Supports.Take(3).Select(x => x.Name));
    }

    [Fact]
    public void A_skill_name_on_two_lines_is_one_skill()
    {
        // "SPECTRAL SHIELD THROW OF" / "TRARTHUS": the OCR reads two lines, neither of them a skill on its own
        var r = MercRecognizer.Read(Load(Path.Combine(Dir, "corvil-bastion.png")), Ocr("corvil-ocr.tsv"), MercData.Default, Icon);
        foreach (var s in r.Warrant.Skills)
            output.WriteLine($"{s.Name}: {string.Join(", ", s.Supports.Select(x => x.TradeText))}");
        foreach (var sl in r.Slots) output.WriteLine($"slot {sl.Skill} x={sl.X} y={sl.Y} s={sl.Size} score={sl.Score:0.00} T{sl.Tier} {sl.Icon}");
        if (Environment.GetEnvironmentVariable("MERC_DEBUG3") is { Length: > 0 } dbg) Debug(r, dbg, "corvil-bastion.png");

        Assert.Equal("Infamous Bastion", r.Warrant.Build);
        Assert.Equal(["Shield Crush", "Shield Charge", "Prismplate", "Spectral Shield Throw of Trarthus", "Dash", "Determination"],
            r.Warrant.Skills.Select(s => s.Name));
        Assert.Equal(5, r.Slots.Count(s => s.Skill == 3));
    }
}
