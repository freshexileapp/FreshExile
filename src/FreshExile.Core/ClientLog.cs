using System.Text;
using System.Text.RegularExpressions;

namespace FreshExile.Core;

public abstract record LogEvent;

/// <summary>"Generating level N area "1_2_town"" — fires before the zone loads.</summary>
public sealed record AreaGenerated(string AreaId, int AreaLevel) : LogEvent
{
    /// <summary>Instance seed: the same map entered again through a portal keeps it, a new map gets another.</summary>
    public long Seed { get; init; }

    /// <summary>Time written at the start of the log line (local), when there is one.</summary>
    public DateTime? At { get; init; }

    public int? Act
    {
        get
        {
            var m = Regex.Match(AreaId, @"^(\d+)_(\d+)_");
            return m.Success ? int.Parse(m.Groups[2].Value) : null;
        }
    }
    public bool IsTown => AreaId.EndsWith("_town", StringComparison.OrdinalIgnoreCase);
    public bool IsMap => AreaId.StartsWith("Map", StringComparison.OrdinalIgnoreCase);
    public bool IsHideout => AreaId.StartsWith("Hideout", StringComparison.OrdinalIgnoreCase);
}

public sealed record ZoneEntered(string Name) : LogEvent;

/// <summary>
/// The areas of the log's tail read at start, oldest first, and the time of its last line: the act / map timer is
/// rebuilt from them instead of starting at the moment the overlay was opened. Only for the timer — nothing else replays.
/// </summary>
public sealed record AreaHistory(IReadOnlyList<AreaGenerated> Areas, DateTime? LastLine) : LogEvent;

public sealed record LevelUp(string Character, string Class, int Level) : LogEvent;

public static partial class ClientLogParser
{
    public static LogEvent? ParseLine(string line)
    {
        var m = GenRx().Match(line);
        if (m.Success)
            return new AreaGenerated(m.Groups[2].Value, int.Parse(m.Groups[1].Value))
            {
                Seed = m.Groups[3].Success && long.TryParse(m.Groups[3].Value, out var seed) ? seed : 0,
                At = LineTime(line),
            };

        m = EnterRx().Match(line);
        if (m.Success) return new ZoneEntered(m.Groups[1].Value);

        m = LevelRx().Match(line);
        if (m.Success) return new LevelUp(m.Groups[1].Value, m.Groups[2].Value, int.Parse(m.Groups[3].Value));

        return null;
    }

    /// <summary>
    /// Newer clients write the live session to LatestClient.txt while Client.txt stays stale;
    /// prefer whichever of the two in the folder was written last.
    /// </summary>
    public static string ResolveLive(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return path;
        var latest = Path.Combine(dir, "LatestClient.txt");
        var client = Path.Combine(dir, "Client.txt");
        if (!File.Exists(latest)) return path;
        if (!File.Exists(client)) return latest;
        return File.GetLastWriteTimeUtc(latest) >= File.GetLastWriteTimeUtc(client) ? latest : client;
    }

    static IEnumerable<string> RunningGameLogs()
    {
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            string? exe = null;
            try
            {
                if (p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase)) exe = p.MainModule?.FileName;
            }
            catch (Exception) { /* access denied */ }
            finally { p.Dispose(); }
            if (exe != null) yield return Path.Combine(Path.GetDirectoryName(exe)!, "logs", "Client.txt");
        }
    }

    public static IEnumerable<string> CandidatePaths()
    {
        string[] roots =
        [
            @"Program Files (x86)\Grinding Gear Games\Path of Exile",
            @"Program Files\Grinding Gear Games\Path of Exile",
            @"Program Files (x86)\Steam\steamapps\common\Path of Exile",
            @"Program Files\Steam\steamapps\common\Path of Exile",
            @"SteamLibrary\steamapps\common\Path of Exile",
            @"Steam\steamapps\common\Path of Exile",
            @"Games\Path of Exile",
            @"Program Files\Epic Games\PathOfExile",
        ];
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            foreach (var r in roots)
                yield return Path.Combine(drive.RootDirectory.FullName, r, "logs", "Client.txt");
    }

    public static string? AutoDetect()
    {
        foreach (var p in RunningGameLogs().Concat(CandidatePaths()))
        {
            try
            {
                var dir = Path.GetDirectoryName(p)!;
                if (File.Exists(p) || File.Exists(Path.Combine(dir, "LatestClient.txt"))) return p;
            }
            catch (IOException) { /* drive not ready */ }
        }
        return null;
    }

    internal static DateTime? LineTime(string line) =>
        line.Length >= 19 && DateTime.TryParseExact(line[..19], "yyyy/MM/dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var t)
            ? t : null;

    [GeneratedRegex(@"Generating level (\d+) area ""([^""]+)""(?: with seed (\d+))?")] private static partial Regex GenRx();
    [GeneratedRegex(@"\] : You have entered (.+?)\.\s*$")] private static partial Regex EnterRx();
    [GeneratedRegex(@"\] : (\S+) \(([^)]+)\) is now level (\d+)")] private static partial Regex LevelRx();
}

/// <summary>Tails Client.txt (read-only, shared) and raises parsed events.</summary>
public sealed class ClientLogWatcher(string path) : IDisposable
{
    const long InitialTail = 4_000_000;

    public event Action<LogEvent>? Event;
    public string Path { get; } = ClientLogParser.ResolveLive(path);

    long _pos;
    string _partial = "";
    Timer? _timer;
    int _busy;

    /// <summary>Reads the tail for the current state, then polls for new lines.</summary>
    public void Start()
    {
        using (var fs = Open())
        {
            var start = Math.Max(0, fs.Length - InitialTail);
            AreaGenerated? area = null; ZoneEntered? zone = null; LevelUp? level = null;
            var areas = new List<AreaGenerated>();
            string? last = null;
            foreach (var line in ReadFrom(fs, start, skipFirst: start > 0))
            {
                if (line.Length > 0) last = line;
                switch (ClientLogParser.ParseLine(line))
                {
                    case AreaGenerated a: area = a; areas.Add(a); break;
                    case ZoneEntered z: zone = z; break;
                    case LevelUp l: level = l; break;
                }
            }
            _pos = fs.Length;
            Event?.Invoke(new AreaHistory(areas, last == null ? null : ClientLogParser.LineTime(last)));
            if (level != null) Event?.Invoke(level);
            if (area != null) Event?.Invoke(area);
            if (zone != null) Event?.Invoke(zone);
        }
        _timer = new Timer(_ => Poll(), null, 500, 500);
    }

    FileStream Open() => new(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    IEnumerable<string> ReadFrom(FileStream fs, long start, bool skipFirst)
    {
        fs.Seek(start, SeekOrigin.Begin);
        using var r = new StreamReader(fs, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        if (skipFirst) r.ReadLine();
        string? line;
        while ((line = r.ReadLine()) != null) yield return line;
    }

    void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            using var fs = Open();
            if (fs.Length < _pos) { _pos = 0; _partial = ""; }
            if (fs.Length == _pos) return;

            fs.Seek(_pos, SeekOrigin.Begin);
            var buf = new byte[fs.Length - _pos];
            var read = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            _pos += read;

            var text = _partial + Encoding.UTF8.GetString(buf, 0, read);
            var lines = text.Split('\n');
            _partial = lines[^1];
            for (var i = 0; i < lines.Length - 1; i++)
                if (ClientLogParser.ParseLine(lines[i].TrimEnd('\r')) is { } ev) Event?.Invoke(ev);
        }
        catch (IOException) { /* game is rotating the file */ }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose() => _timer?.Dispose();
}
