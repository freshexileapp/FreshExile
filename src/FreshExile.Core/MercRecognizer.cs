using System.Globalization;
using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>A picture as 32-bit BGRA pixels, rows top-down, no padding (a screen capture, a decoded icon).</summary>
public sealed record Pixels(byte[] Bgra, int Width, int Height)
{
    public int Index(int x, int y) => (y * Width + x) * 4;
}

/// <summary>A line the OCR read, in the picture's pixels.</summary>
public sealed record OcrLine(string Text, double X, double Y, double W, double H)
{
    public double Bottom => Y + H;
    public double Right => X + W;
    public double CenterY => Y + H / 2;
}

/// <summary>A support a mercenary skill can carry: "Greater Multistrike", tier 3, its icon on the CDN.</summary>
public sealed record MercSupportInfo(string Id, string Name, int Tier, string Icon);

/// <summary>
/// The mercenaries' builds, skills and the supports each skill can carry, from Data/Mercenaries.tsv
/// (tools/update-merc-data.ps1, RePoE).
/// </summary>
public sealed class MercData
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Builds { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<MercSupportInfo>> SkillSupports { get; }
    /// <summary>A support's icon path on the CDN by its name ("Greater Brutality"); null for a name not in the data.</summary>
    public string? IconOf(string support) =>
        (_icons ??= SkillSupports.Values.SelectMany(s => s).GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Icon, StringComparer.OrdinalIgnoreCase)).GetValueOrDefault(support);
    Dictionary<string, string>? _icons;

    public IReadOnlyCollection<string> Skills =>SkillSupports.Keys.Concat(Builds.Values.SelectMany(s => s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    MercData(Dictionary<string, IReadOnlyList<string>> builds, Dictionary<string, IReadOnlyList<MercSupportInfo>> skills)
    {
        Builds = builds;
        SkillSupports = skills;
    }

    static readonly Lazy<MercData> Embedded = new(() =>
    {
        using var s = typeof(MercData).Assembly.GetManifestResourceStream("Mercenaries.tsv");
        return Load(s == null ? null : new StreamReader(s));
    });

    public static MercData Default => Embedded.Value;

    public static MercData Load(TextReader? r)
    {
        var builds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var skillIds = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var supports = new Dictionary<string, MercSupportInfo>(StringComparer.Ordinal);
        if (r != null)
            using (r)
                while (r.ReadLine() is { } line)
                {
                    var p = line.Split('\t');
                    switch (p)
                    {
                        case ["B", var name, var pool]: builds[name] = pool.Split(" | ", StringSplitOptions.RemoveEmptyEntries); break;
                        case ["K", var skill, var ids]: skillIds[skill] = ids.Split(" | ", StringSplitOptions.RemoveEmptyEntries); break;
                        case ["S", var id, var name, var tier, var icon]:
                            supports[id] = new MercSupportInfo(id, name, int.Parse(tier, CultureInfo.InvariantCulture), icon);
                            break;
                    }
                }
        var skills = skillIds.ToDictionary(k => k.Key, k => (IReadOnlyList<MercSupportInfo>)k.Value.Where(supports.ContainsKey).Select(id => supports[id]).ToList(),
            StringComparer.OrdinalIgnoreCase);
        return new MercData(builds, skills);
    }
}

/// <summary>What was read off the mercenary window, with what could not be told for sure.</summary>
public sealed record MercReading(MercenaryWarrant Warrant, IReadOnlyList<string> Notes)
{
    /// <summary>Supports whose icon several candidates share (the generic gold / silver ones): every one of them.</summary>
    public IReadOnlyDictionary<(int Skill, int Slot), IReadOnlyList<MercenarySupport>> Alternatives { get; init; } =
        new Dictionary<(int, int), IReadOnlyList<MercenarySupport>>();

    /// <summary>Where each support was found and how sure the match is: for the window's marks and for tuning.</summary>
    public IReadOnlyList<MercSlot> Slots { get; init; } = [];
}

/// <summary>A support's icon on the screen: skill row, the box, the match (-1..1), the icon it matched.</summary>
public sealed record MercSlot(int Skill, int X, int Y, int Size, double Score, string Icon, int Tier);

/// <summary>
/// Reads the in-game mercenary window (the one with "Take Item" / "Rematch"): its header and skill names from the OCR,
/// each skill's supports by their icons, among the supports that skill can carry, and their tier by the roman numeral
/// drawn over the icon's corner.
/// </summary>
public static partial class MercRecognizer
{
    /// <summary>Where each row's search went, for the log (null = quiet).</summary>
    public static Action<string>? Trace { get; set; }

    /// <param name="icon">The CDN icon for an <see cref="MercSupportInfo.Icon"/> path; null when it is not at hand.</param>
    public static MercReading Read(Pixels shot, IReadOnlyList<OcrLine> lines, MercData data, Func<string, Pixels?> icon)
    {
        var notes = new List<string>();
        // the build: the longest build name some line reads as ("Infamous Shock Ambusher", not "Shock Ambusher")
        var build = data.Builds.Keys
            .Select(b => (Build: b, Line: lines.FirstOrDefault(l => Close(Letters(l.Text), Letters(b)))))
            .Where(x => x.Line != null).OrderByDescending(x => x.Build.Length).Select(x => x.Build).FirstOrDefault() ?? "";
        if (build.Length == 0) notes.Add(Loc.T("Не прочитал билд наёмника"));

        var level = lines.Select(l => LevelRx().Match(l.Text)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .FirstOrDefault();
        var name = lines.Where(l => l.Text.Contains(", ", StringComparison.Ordinal) && !l.Text.TrimStart().StartsWith('"') && !l.Text.Contains('\''))
            .OrderBy(l => l.Y).Select(l => l.Text.Trim()).FirstOrDefault() ?? "";

        // the skills: lines that read as one of the build's skills (any mercenary skill when the build is not read)
        var pool = build.Length > 0 ? data.Builds[build] : data.Skills.ToList();
        var skillLines = new List<(OcrLine Line, string Skill)>();
        (string Skill, int D) Best(OcrLine l)
        {
            var t = Letters(l.Text);
            return t.Length < 3 ? ("", int.MaxValue) : pool.Select(s => (Skill: s, D: Distance(t, Letters(s)))).OrderBy(x => x.D).First();
        }
        var ordered = lines.OrderBy(l => l.Y).ToList();
        for (var k = 0; k < ordered.Count; k++)
        {
            var l = ordered[k];
            var best = Best(l);
            // a long name wraps ("SPECTRAL SHIELD THROW OF" / "TRARTHUS"): the line right under it, at the same left edge,
            // read together with it; the row's icons sit level with the middle of the two
            if (ordered.FirstOrDefault(n => n != l && Math.Abs(n.X - l.X) < l.H && n.Y > l.Y && n.Y - l.Bottom < l.H * 1.2) is { } wrap)
            {
                var both = new OcrLine(l.Text + " " + wrap.Text, Math.Min(l.X, wrap.X), l.Y,
                    Math.Max(l.Right, wrap.Right) - Math.Min(l.X, wrap.X), wrap.Bottom - l.Y);
                var joined = Best(both);
                if (joined.D <= Tolerance(joined.Skill) && joined.D < best.D) { l = both; best = joined; ordered.Remove(wrap); }
            }
            if (best.D <= Tolerance(best.Skill) && skillLines.All(s => s.Skill != best.Skill)) skillLines.Add((l, best.Skill));
        }
        if (skillLines.Count == 0) notes.Add(Loc.T("Не нашёл умений наёмника: выдели окно с его умениями"));

        // a row's height: the step between skill lines (the icons are about two thirds of it)
        var steps = skillLines.Zip(skillLines.Skip(1), (a, b) => b.Line.CenterY - a.Line.CenterY).Where(d => d > 0).OrderBy(d => d).ToList();
        var row = steps.Count > 0 ? steps[steps.Count / 2] : skillLines.Count > 0 ? skillLines[0].Line.H * 4 : 40;

        var skills = new List<MercenarySkill>();
        var alternatives = new Dictionary<(int, int), IReadOnlyList<MercenarySupport>>();
        var slots = new List<MercSlot>();
        for (var i = 0; i < skillLines.Count; i++)
        {
            var (line, skill) = skillLines[i];
            var candidates = data.SkillSupports.TryGetValue(skill, out var c) ? c : [];
            var found = Supports(shot, line, row, candidates, icon, i, slots);
            var list = new List<MercenarySupport>();
            foreach (var (picked, others) in found)
            {
                if (others.Count > 1) alternatives[(i, list.Count)] = others.Select(o => new MercenarySupport(o.Name, o.Tier)).ToList();
                list.Add(new MercenarySupport(picked.Name, picked.Tier));
            }
            skills.Add(new MercenarySkill(skill, list));
        }
        return new MercReading(new MercenaryWarrant(name, build, level, skills), notes) { Alternatives = alternatives, Slots = slots };
    }

    /// <summary>
    /// The icons right of a skill's name, left to right: each slot's best icon among the skill's candidates (one per
    /// picture: tiers share it), then its tier from the numeral. A slot with no icon close enough ends the row.
    /// </summary>
    static List<(MercSupportInfo Picked, List<MercSupportInfo> Same)> Supports(Pixels shot, OcrLine line, double row,
        IReadOnlyList<MercSupportInfo> candidates, Func<string, Pixels?> icon, int skillIndex, List<MercSlot> slots)
    {
        var result = new List<(MercSupportInfo, List<MercSupportInfo>)>();
        var families = candidates.GroupBy(c => c.Icon).Select(g => (Icon: g.Key, Art: icon(g.Key), Members: g.ToList()))
            .Where(f => f.Art != null).ToList();
        if (families.Count == 0) return result;

        var size = (int)Math.Round(row * 0.66);
        if (size < 8) return result;
        // the templates at a few sizes around two thirds of the row: the slot's art fills it more or less
        var sizes = new[] { 0.66, 0.7, 0.74, 0.78 }.Select(k => (int)Math.Round(row * k)).Distinct().ToList();
        var templates = families.SelectMany(f => sizes.Select(s => (Family: f, Size: s, T: Template.From(f.Art!, s)))).ToList();

        var cy = line.CenterY;
        var x0 = (int)(line.Right + row * 0.4);
        var big = sizes.Max();

        // a slot holds an icon when its middle is bright and busy; an empty slot is a flat dark frame (spread 0-1 against
        // 40-60 for any icon), whatever an icon template would score there
        bool Filled(int x)
        {
            int y = (int)Math.Round(cy - size / 2.0);
            if (x < 0 || y < 0 || x + size > shot.Width || y + size > shot.Height) return false;
            double sum = 0, sq = 0;
            var n = 0;
            for (var dy = size / 5; dy < size * 4 / 5; dy++)
                for (var dx = size / 5; dx < size * 4 / 5; dx++)
                {
                    var i = shot.Index(x + dx, y + dy);
                    var l = 0.114 * shot.Bgra[i] + 0.587 * shot.Bgra[i + 1] + 0.299 * shot.Bgra[i + 2];
                    sum += l; sq += l * l; n++;
                }
            var mean = sum / n;
            return Math.Sqrt(Math.Max(0, sq / n - mean * mean)) > 15;
        }

        // the icon that fits a spot best, the spot nudged a few pixels either way
        (int X, int Y, double Score, int Index) BestAt(int from, int to)
        {
            (int, int, double, int) best = (from, 0, double.MinValue, -1);
            for (var x = Math.Max(0, from); x <= Math.Min(shot.Width - big, to); x++)
                for (var dy = -2; dy <= 2; dy += 2)
                    for (var t = 0; t < templates.Count; t++)
                    {
                        var tpl = templates[t];
                        var y = (int)Math.Round(cy - tpl.Size / 2.0) + dy;
                        if (y < 0 || y + tpl.Size > shot.Height) continue;
                        var sc = tpl.T.Match(shot, x, y);
                        if (sc > best.Item3) best = (x, y, sc, t);
                    }
            return best;
        }

        // the first slot: the first busy spot right of the name; then one per row step, until an empty slot
        var picked = new List<(int X, int Y, double Score, int Index)>();
        var start = -1;
        for (var x = x0; x <= Math.Min(shot.Width - size, x0 + row * 4); x++)
            if (Filled(x)) { start = x; break; }
        Trace?.Invoke(FormattableString.Invariant($"row {line.Text}: x0 {x0}, step {row:0.0}, {families.Count} icons, first busy at {start}"));
        if (start >= 0)
        {
            // the box turns busy as soon as it overlaps the icon's left edge: the icon itself is up to a width further on
            var at = BestAt(start, start + size);
            while (at.Index >= 0 && picked.Count < 5)
            {
                picked.Add(at);
                var next = (int)Math.Round(at.X + row);
                if (!Filled(next)) break;
                at = BestAt(next - (int)(row * 0.15), next + (int)(row * 0.15));
            }
        }
        foreach (var p in picked.OrderBy(p => p.X))
        {
            var (family, tplSize, _) = templates[p.Index];
            // the numeral sits on the slot, not on whichever template size fitted best: its box from the icon's centre
            var box = (int)Math.Round(row * 0.72);
            int bx = p.X + tplSize / 2 - box / 2, by = p.Y + tplSize / 2 - box / 2;
            var tier = Tier(shot, bx, by, box);
            slots.Add(new MercSlot(skillIndex, bx, by, box, p.Score, family.Icon, tier));
            List<MercSupportInfo> OfTier(List<MercSupportInfo> members)
            {
                var same = members.Where(m => m.Tier == tier).ToList();
                return same.Count > 0 ? same : members.OrderBy(m => Math.Abs(m.Tier - tier)).ThenByDescending(m => m.Tier).Take(1).ToList();
            }
            var byTier = OfTier(family.Members);
            // a look-alike that scores nearly as well here (the crit chance wheel and the crit damage one) comes along as
            // a choice: a support is unticked anyway, a wrong single guess would hide the right one
            var rival = templates.Where(t => t.Family.Icon != family.Icon)
                .Select(t => (t.Family, Score: t.T.Match(shot, Math.Clamp(p.X + tplSize / 2 - t.Size / 2, 0, shot.Width - t.Size),
                    Math.Clamp(p.Y + tplSize / 2 - t.Size / 2, 0, shot.Height - t.Size))))
                .OrderByDescending(t => t.Score).FirstOrDefault();
            if (rival.Family.Members != null && rival.Score > p.Score - 0.04) byTier = [.. byTier, .. OfTier(rival.Family.Members)];
            result.Add((byTier[0], byTier));
        }
        return result;
    }

    /// <summary>
    /// The numeral in the icon's lower right corner, "I" to "III". Its outer strokes are a bright cream gold (FED58B),
    /// they mark where it stands; inside, a stroke is a column of gold that stays the same down its middle (the middle
    /// "I" of "III" is dimmer, AF9362; the icon's own gold there, a bolt or a cross, is slanted and uneven).
    /// </summary>
    public static int Tier(Pixels shot, int x, int y, int size, Action<string>? trace = null)
    {
        int left = x + size * 2 / 5, right = Math.Min(shot.Width, x + size + size / 8);
        int top = y + size / 2, bottom = Math.Min(shot.Height, y + size + size / 8);
        (int R, int G, int B) At(int px, int py)
        {
            var i = shot.Index(px, py);
            return (shot.Bgra[i + 2], shot.Bgra[i + 1], shot.Bgra[i]);
        }
        static bool Bright((int R, int G, int B) c) => c.R > 225 && c.G > 180 && c.B > 100 && c.B < 175 && c.R - c.B > 70;
        static bool Warm((int R, int G, int B) c) => c.R > 140 && c.G > 100 && c.R - c.B > 45 && c.B < c.G;

        // its foot: the lowest bright pixel (the icon may have bright spots higher up); it is about a quarter of the icon tall
        var maxY = -1;
        for (var py = top; py < bottom; py++)
            for (var px = left; px < right; px++)
                if (Bright(At(px, py))) maxY = py;
        if (maxY < 0) return 2;
        var tall = Math.Max(4, (int)Math.Round(size * 0.26));
        int minX = int.MaxValue, maxX = -1;
        for (var py = maxY - tall; py <= maxY; py++)
            for (var px = left; px < right; px++)
                if (Bright(At(px, py))) { minX = Math.Min(minX, px); maxX = Math.Max(maxX, px); }
        // the strokes' middle, clear of the serifs at the top and the foot
        int mid0 = maxY - tall * 7 / 10, mid1 = maxY - tall * 3 / 10;
        trace?.Invoke($"box x {minX}..{maxX} foot {maxY} mid {mid0}..{mid1}");
        var rows = mid1 - mid0 + 1;
        var runs = 0;
        var inRun = false;
        for (var px = minX; px <= maxX; px++)
        {
            var reds = new List<int>();
            for (var py = mid0; py <= mid1; py++)
                if (At(px, py) is var c && Warm(c)) reds.Add(c.R);
            var even = reds.Count > 0 && reds.Max() - reds.Min() <= 40;
            var on = reds.Count * 10 >= rows * 6 && even;
            trace?.Invoke($"x={px} warm={reds.Count}/{rows} reds={string.Join(",", reds)} on={on}");
            if (on && !inRun) runs++;
            inRun = on;
        }
        return Math.Clamp(runs, 1, 3);
    }

    /// <summary>An icon scaled to a slot, its opaque pixels' colour centred for a normalised correlation.</summary>
    sealed class Template
    {
        int _size;
        readonly List<(int Dx, int Dy, double R, double G, double B)> _px = [];
        double _norm;

        public static Template From(Pixels art, int size)
        {
            var t = new Template { _size = size };
            var acc = new List<(int, int, double, double, double)>();
            for (var dy = 0; dy < size; dy++)
                for (var dx = 0; dx < size; dx++)
                {
                    // the numeral's corner is left out: it is drawn over the art in the game, not in the CDN icon
                    if (dx > size * 0.45 && dy > size * 0.55) continue;
                    // box-sample the source square under this pixel
                    int sx0 = dx * art.Width / size, sx1 = Math.Max(sx0 + 1, (dx + 1) * art.Width / size);
                    int sy0 = dy * art.Height / size, sy1 = Math.Max(sy0 + 1, (dy + 1) * art.Height / size);
                    double r = 0, g = 0, b = 0, a = 0;
                    for (var sy = sy0; sy < sy1; sy++)
                        for (var sx = sx0; sx < sx1; sx++)
                        {
                            var i = art.Index(sx, sy);
                            var al = art.Bgra[i + 3] / 255.0;
                            b += art.Bgra[i] * al; g += art.Bgra[i + 1] * al; r += art.Bgra[i + 2] * al; a += al;
                        }
                    var n = (sx1 - sx0) * (sy1 - sy0);
                    if (a / n < 0.8) continue;
                    acc.Add((dx, dy, r / a, g / a, b / a));
                }
            if (acc.Count == 0) return t;
            double mr = acc.Average(p => p.Item3), mg = acc.Average(p => p.Item4), mb = acc.Average(p => p.Item5);
            foreach (var (dx, dy, r, g, b) in acc) t._px.Add((dx, dy, r - mr, g - mg, b - mb));
            t._norm = Math.Sqrt(t._px.Sum(p => p.R * p.R + p.G * p.G + p.B * p.B));
            return t;
        }

        /// <summary>Normalised cross-correlation with the screen at (x, y), -1..1.</summary>
        public double Match(Pixels shot, int x, int y)
        {
            if (_px.Count == 0 || _norm == 0) return -1;
            double sr = 0, sg = 0, sb = 0;
            foreach (var p in _px)
            {
                var i = shot.Index(x + p.Dx, y + p.Dy);
                sb += shot.Bgra[i]; sg += shot.Bgra[i + 1]; sr += shot.Bgra[i + 2];
            }
            double mr = sr / _px.Count, mg = sg / _px.Count, mb = sb / _px.Count;
            double dot = 0, ss = 0;
            foreach (var p in _px)
            {
                var i = shot.Index(x + p.Dx, y + p.Dy);
                double b = shot.Bgra[i] - mb, g = shot.Bgra[i + 1] - mg, r = shot.Bgra[i + 2] - mr;
                dot += r * p.R + g * p.G + b * p.B;
                ss += r * r + g * g + b * b;
            }
            return ss == 0 ? -1 : dot / (_norm * Math.Sqrt(ss));
        }
    }

    /// <summary>Upper case letters and single spaces: "Summon Skitterbots" and the OCR's "SUMMON SKITTERBOTS" meet.</summary>
    static string Letters(string s) => SpacesRx().Replace(NotLetterRx().Replace(s.ToUpperInvariant(), " "), " ").Trim();

    static bool Close(string a, string b) => a.Length > 0 && Distance(a, b) <= Math.Max(1, b.Length / 8);
    static int Tolerance(string skill) => Math.Max(1, skill.Length / 8);

    static int Distance(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) d[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = d[0];
            d[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cur = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = cur;
            }
        }
        return d[b.Length];
    }

    // "Lvl 83" as the OCR reads it: "LVI 83", "Lvl 83", "LV1 83"
    [GeneratedRegex(@"\bL[VvIl1|]{1,2}[l1I|]?\s*(\d{1,3})\b")] private static partial Regex LevelRx();
    [GeneratedRegex(@"[^A-Z]")] private static partial Regex NotLetterRx();
    [GeneratedRegex(@"\s+")] private static partial Regex SpacesRx();
}
