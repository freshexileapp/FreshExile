using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>
/// The in-game mercenary window off the screen: the picked rectangle cut out of a capture, its text read by Windows'
/// own OCR (English, three times enlarged: the build line is small), the support icons fetched once from the game's CDN.
/// </summary>
public static class MercScan
{
    const int Enlarge = 3;

    /// <summary>A rectangle of a screen capture (BGRA, top-down), alpha made opaque for the OCR.</summary>
    public static Pixels Crop(byte[] screen, int screenWidth, int screenHeight, Int32Rect r)
    {
        int x0 = Math.Clamp(r.X, 0, screenWidth - 1), y0 = Math.Clamp(r.Y, 0, screenHeight - 1);
        int w = Math.Clamp(r.Width, 1, screenWidth - x0), h = Math.Clamp(r.Height, 1, screenHeight - y0);
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++) Buffer.BlockCopy(screen, ((y0 + y) * screenWidth + x0) * 4, px, y * w * 4, w * 4);
        for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        return new Pixels(px, w, h);
    }

    /// <summary>The lines Windows' OCR reads, in the crop's own pixels; empty when no English recognizer is installed.</summary>
    public static async Task<IReadOnlyList<OcrLine>> ReadTextAsync(Pixels crop)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                     ?? Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) return [];
        var big = Scale(crop, Enlarge);
        var bitmap = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(big.Bgra.AsBuffer(),
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, big.Width, big.Height, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double x0 = line.Words.Min(w => w.BoundingRect.X), y0 = line.Words.Min(w => w.BoundingRect.Y);
            double x1 = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width), y1 = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            lines.Add(new OcrLine(line.Text, x0 / Enlarge, y0 / Enlarge, (x1 - x0) / Enlarge, (y1 - y0) / Enlarge));
        }
        return lines;
    }

    /// <summary>Bilinear enlargement: Windows' OCR misses text under about 20 pixels tall.</summary>
    static Pixels Scale(Pixels src, int k)
    {
        int w = src.Width * k, h = src.Height * k;
        var dst = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var fy = Math.Max(0, (y + 0.5) / k - 0.5);
            int y0 = Math.Min((int)fy, src.Height - 1), y1 = Math.Min(y0 + 1, src.Height - 1);
            var ty = fy - y0;
            for (var x = 0; x < w; x++)
            {
                var fx = Math.Max(0, (x + 0.5) / k - 0.5);
                int x0 = Math.Min((int)fx, src.Width - 1), x1 = Math.Min(x0 + 1, src.Width - 1);
                var tx = fx - x0;
                for (var c = 0; c < 4; c++)
                {
                    double a = src.Bgra[src.Index(x0, y0) + c], b = src.Bgra[src.Index(x1, y0) + c];
                    double d = src.Bgra[src.Index(x0, y1) + c], e = src.Bgra[src.Index(x1, y1) + c];
                    dst[(y * w + x) * 4 + c] = (byte)Math.Round((a * (1 - tx) + b * tx) * (1 - ty) + (d * (1 - tx) + e * tx) * ty);
                }
            }
        }
        return new Pixels(dst, w, h);
    }

    static string IconDir => Path.Combine(StateStore.Dir, "merc-icons");

    /// <summary>
    /// The support icons these skills can show, from disk or the game's CDN (web.poecdn.com/image/Art/…png, about 10 KB
    /// each, kept for good): the recognizer matches the window's icons against them.
    /// </summary>
    public static async Task<Dictionary<string, Pixels>> IconsAsync(HttpClient http, IEnumerable<string> skills, MercData data)
    {
        var paths = skills.SelectMany(s => data.SkillSupports.TryGetValue(s, out var c) ? c : []).Select(c => c.Icon).Distinct().ToList();
        var result = new Dictionary<string, Pixels>(StringComparer.Ordinal);
        Directory.CreateDirectory(IconDir);
        await Task.WhenAll(paths.Select(async p =>
        {
            var file = Path.Combine(IconDir, p.Replace('/', '_') + ".png");
            try
            {
                if (!File.Exists(file))
                {
                    var bytes = await http.GetByteArrayAsync($"https://web.poecdn.com/image/Art/{p}.png");
                    await File.WriteAllBytesAsync(file, bytes);
                }
                var px = Decode(file);
                lock (result) result[p] = px;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException or TaskCanceledException)
            {
                Log.Write($"merc icon {p}: {ex.Message}");
            }
        }));
        return result;
    }

    static Pixels Decode(string file)
    {
        using var fs = File.OpenRead(file);
        var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var px = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(px, bgra.PixelWidth * 4, 0);
        return new Pixels(px, bgra.PixelWidth, bgra.PixelHeight);
    }

    /// <summary>
    /// The window read as a warrant: the skills found first (no icons needed for the names), then their supports with
    /// the icons fetched; a support whose icon several share brings every one of them along (all unticked anyway).
    /// </summary>
    public static async Task<MercReading> ReadAsync(HttpClient http, Pixels crop)
    {
        var data = MercData.Default;
        MercRecognizer.Trace = m => Log.Write("merc " + m);
        var lines = await ReadTextAsync(crop);
        var names = await Task.Run(() => MercRecognizer.Read(crop, lines, data, _ => null));
        var icons = await IconsAsync(http, names.Warrant.Skills.Select(s => s.Name), data);
        var reading = await Task.Run(() => MercRecognizer.Read(crop, lines, data, p => icons.GetValueOrDefault(p)));
        var skills = reading.Warrant.Skills.Select((s, i) => s with
        {
            Supports = s.Supports.SelectMany((sup, j) => reading.Alternatives.TryGetValue((i, j), out var alt) ? alt : [sup]).Distinct().ToList(),
        }).ToList();
        foreach (var l in lines) Log.Write(FormattableString.Invariant($"merc ocr: {l.X:0},{l.Y:0} {l.W:0}x{l.H:0} {l.Text}"));
        foreach (var sl in reading.Slots) Log.Write(FormattableString.Invariant($"merc slot: skill {sl.Skill} at {sl.X},{sl.Y} size {sl.Size} score {sl.Score:0.00} tier {sl.Tier} {sl.Icon}"));
        Log.Write($"merc scan: {lines.Count} lines, build \"{reading.Warrant.Build}\", skills {skills.Count}, supports {skills.Sum(s => s.Supports.Count)}");
        return reading with { Warrant = reading.Warrant with { Skills = skills } };
    }
}
