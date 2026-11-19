using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>
/// Keeps the last few screenshots of moments that matter for the tree overlay (opened, closed, zoom re-fit),
/// so a bug report shows what the tool actually saw. Frames stay on this PC unless a report is made.
/// </summary>
public static class Recorder
{
    const int Keep = 8;
    static readonly string Dir = Path.Combine(StateStore.Dir, "frames");
    static DateTime _last;
    static bool _lastGood;

    public static void Note(TreeVerdict v, bool open, byte[] bgra, int w, int h)
    {
        string? tag = null;
        if (v.SweepRan) tag = v.NewScale ? "rescale" : "sweep-miss";
        else if (v.Good && !open) tag = "open";
        else if (!v.Good && open) tag = "lost";
        else if (v.Good != _lastGood) tag = v.Good ? "good" : "bad";
        _lastGood = v.Good;
        if (tag == null || (DateTime.UtcNow - _last).TotalSeconds < 3) return;
        _last = DateTime.UtcNow;

        try
        {
            Directory.CreateDirectory(Dir);
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bgra, w * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            var file = Path.Combine(Dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{tag}.png");
            using (var fs = File.Create(file)) enc.Save(fs);
            foreach (var old in Directory.GetFiles(Dir, "*.png").OrderByDescending(f => f).Skip(Keep)) File.Delete(old);
        }
        catch (Exception ex) { Log.Write("frame save failed: " + ex.Message); }
    }

    public static IEnumerable<string> Frames() =>
        Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.png").OrderBy(f => f) : [];
}

/// <summary>
/// "Save report": one zip on the desktop with the logs and settings, user folders, account and character
/// masked (<see cref="Privacy"/>). Screenshots only on request: chat and nicknames can be on them.
/// </summary>
public static class Report
{
    public static string Create(AppState state, bool includeFrames)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"FreshExile-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        string Clean(string text) => Privacy.Scrub(text, state.Account, state.Character);

        foreach (var name in new[] { "overlay.log.old", "overlay.log", "crash.log" })
        {
            var f = Path.Combine(StateStore.Dir, name);
            if (!File.Exists(f)) continue;
            string text;
            try { text = File.ReadAllText(f); }
            catch (IOException) { continue; }
            Write(zip, name, Clean(text));
        }
        if (includeFrames)
            foreach (var f in Recorder.Frames()) zip.CreateEntryFromFile(f, "frames/" + Path.GetFileName(f));

        // settings without personal bits: the account and character name stay home
        var json = JsonSerializer.Serialize(state);
        var copy = JsonSerializer.Deserialize<AppState>(json)!;
        copy.Account = copy.Account.Length > 0 ? "(set)" : "";
        copy.Character = copy.Character.Length > 0 ? "(set)" : "";
        copy.ClientLogPath = Privacy.ShortPath(copy.ClientLogPath);
        // the recent builds' XML stays home: the titles tell enough, the current build is in BuildXml
        foreach (var r in copy.RecentBuilds) r.Xml = "";
        Write(zip, "state.json", Clean(JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true })));
        Write(zip, "about.txt", About() + $"Frames: {(includeFrames ? "attached" : "not attached")}{Environment.NewLine}");
        return path;
    }

    public static string About()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FreshExile {Assembly.GetExecutingAssembly().GetName().Version}");
        sb.AppendLine($"OS: {Environment.OSVersion}, .NET {Environment.Version}");
        sb.AppendLine($"Primary screen (DIP): {SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}");
        if (Application.Current?.MainWindow is { } w)
        {
            var dpi = VisualTreeHelper.GetDpi(w);
            sb.AppendLine($"DPI scale: {dpi.DpiScaleX:F2}");
        }
        sb.AppendLine($"Time: {DateTime.Now:O}");
        return sb.ToString();
    }

    static void Write(ZipArchive zip, string name, string text)
    {
        using var s = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        s.Write(text);
    }
}

/// <summary>Unexpected exceptions go to crash.log and a toast; the overlay keeps running.</summary>
public static class CrashGuard
{
    static readonly string FilePath = Path.Combine(StateStore.Dir, "crash.log");

    public static void Install(Application app, Func<MainViewModel?> vm)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write("ui", e.Exception);
            e.Handled = true;
            vm()?.Toast(Loc.T("Ошибка записана в crash.log — тулза продолжает работать"));
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("domain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("task", e.Exception);
            e.SetObserved();
        };
    }

    static void Write(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(StateStore.Dir);
            File.AppendAllText(FilePath, $"--- {DateTime.Now:O} [{source}]{Environment.NewLine}{ex}{Environment.NewLine}");
            Log.Write($"crash [{source}]: {ex?.GetType().Name}: {ex?.Message}");
        }
        catch (IOException) { /* nothing left to do */ }
    }
}
