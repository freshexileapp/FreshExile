using System.Text.Json;

namespace FreshExile.Core;

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class AppState
{
    /// <summary>Interface language, "ru" or "en"; null until the first start picks it from Windows.</summary>
    public string? Language { get; set; }

    /// <summary>The interface's own zoom on top of Windows' scaling: 1 = as designed (Settings → General).</summary>
    public double UiScale { get; set; } = 1.0;

    public string BuildInput { get; set; } = "";
    public string? BuildXml { get; set; }
    public string? BuildId { get; set; }

    /// <summary>Builds loaded before, newest first (the current one among them): the build itself is kept, not just its link.</summary>
    public List<RecentBuild> RecentBuilds { get; set; } = new();

    /// <summary>Names searched on the trade by name (the search window), newest first.</summary>
    public List<string> RecentSearches { get; set; } = new();

    /// <summary>Trade league to search in; empty = the current softcore league from the trade site.</summary>
    public string? TradeLeague { get; set; }

    /// <summary>Tree header list unfolded to all points (folded: the next five).</summary>
    public bool TreeStepsExpanded { get; set; }

    /// <summary>Trade listing filter (see <see cref="TradeLink.Status"/>); empty = Instant Buyout.</summary>
    public string? TradeStatus { get; set; }

    /// <summary>Trade stat minimums, % of PoB's roll; null = 80.</summary>
    public int? TradeStrictness { get; set; }

    /// <summary>Price check: trade "Buyout Price" option ("" any, "chaos", "divine", "chaos_divine").</summary>
    public string PriceCurrency { get; set; } = "";

    /// <summary>Price check: listed within ("" any time, "1day", "3days", "1week").</summary>
    public string PriceListed { get; set; } = "";

    /// <summary>Price check: one line per seller.</summary>
    public bool PriceCollapse { get; set; } = true;

    /// <summary>Price check: search right away when the window opens.</summary>
    public bool PriceAutoSearch { get; set; } = true;

    /// <summary>Top-mod lists edited by the player, slot key → phrases; a slot missing here keeps its default.</summary>
    public Dictionary<string, List<string>> TopMods { get; set; } = new();

    /// <summary>A slot's top line counts in this upper part of its mod's tiers: 3 = a third, 2 = a half, 0 = any tier.</summary>
    public int TopModShare { get; set; } = 3;
    public int StageIndex { get; set; }
    public Dictionary<string, HashSet<string>> Bought { get; set; } = new();

    /// <summary>Per build: stages whose socket changes the user has already made.</summary>
    public Dictionary<string, HashSet<int>> SwapDone { get; set; } = new();

    public string ClientLogPath { get; set; } = "";
    public bool FollowGame { get; set; } = true;

    /// <summary>
    /// Following the game at all: stage switches from the log, the in-game tree overlay (screen capture), the inventory dodge,
    /// new-character and new-stage prompts and the town list. Off = a plain guide plus the price check and the run clock.
    /// </summary>
    public bool Tracking { get; set; } = true;

    /// <summary>Under <see cref="Tracking"/>: the town list of gems to buy and the gems on the stage-switch plate.</summary>
    public bool TrackGems { get; set; } = true;

    /// <summary>Under <see cref="Tracking"/>: socket changes (what to swap) in town and on the stage-switch plate.</summary>
    public bool TrackLinks { get; set; } = true;

    /// <summary>Panel and tree window fade out while another program (not the game) is in front.</summary>
    public bool HideWhenGameInactive { get; set; } = true;
    public string Account { get; set; } = "";
    public string Character { get; set; } = "";
    public bool CompareWithCharacter { get; set; }

    /// <summary>
    /// "From stage 1" was taken for a new character: until its profile is synced nothing is assumed taken, not even the
    /// previous stage's tree — the path runs from the class start, and what is taken is what the overlay sees gold.
    /// </summary>
    public bool FreshCharacter { get; set; }
    public bool HideBought { get; set; }

    /// <summary>Per build: stage index → start level set by the user (overrides the title).</summary>
    public Dictionary<string, Dictionary<int, int>> StageLevels { get; set; } = new();

    /// <summary>Per build: stage index → map tier set by the user (overrides the title).</summary>
    public Dictionary<string, Dictionary<int, int>> StageTiers { get; set; } = new();

    /// <summary>Action id → "Ctrl+Alt+G"; missing = default, empty = unbound.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// <summary>Draw the plan right on the in-game tree when it is open.</summary>
    public bool TreeOverlay { get; set; } = true;

    /// <summary>The card of a cluster jewel on the in-game tree lists every mod, not only the jewel and its notables.</summary>
    public bool ClusterCardFull { get; set; }

    /// <summary>Check GitHub for a newer release and put it in place of the exe (the next start runs it).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Legacy single calibrated zoom; migrated into TreeScales.</summary>
    public double? TreeScale { get; set; }

    /// <summary>In-game tree zoom levels seen so far (world → screen px) for TreeScaleHeight, latest first.</summary>
    public List<double> TreeScales { get; set; } = new();
    public int TreeScaleHeight { get; set; }

    /// <summary>Thumbnail of the "Skills" title strip: "tree is open" even at an unknown zoom.</summary>
    public byte[]? TreeHeaderPatch { get; set; }

    /// <summary>Guide panel collapsed to the stage block.</summary>
    public bool Compact { get; set; }

    public double TreeBgOpacity { get; set; } = 0.97;
    public WindowPlacement? Main { get; set; }

    /// <summary>Where the mini tracker sits; independent of the full panel.</summary>
    public WindowPlacement? MainMini { get; set; }
    public WindowPlacement? Tree { get; set; }
}

public static class StateStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>%APPDATA%\FreshExile; FRESHEXILE_DATA points a QA run elsewhere so it never touches the real state.</summary>
    public static string Dir { get; } = Environment.GetEnvironmentVariable("FRESHEXILE_DATA") is { Length: > 0 } qa
        ? qa
        : UserDataDir();

    /// <summary>The app was called PoE Leveling Overlay before: its folder moves over once, so the build, stage and windows stay.</summary>
    static string UserDataDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "FreshExile");
        var old = Path.Combine(appData, "PoeLevelingOverlay");
        try
        {
            if (!Directory.Exists(dir) && Directory.Exists(old))
                Directory.Move(old, dir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return dir;
    }

    static string FilePath => Path.Combine(Dir, "state.json");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath), Json) ?? new AppState();
        }
        catch (Exception) { /* corrupted → start clean */ }
        return new AppState();
    }

    public static void Save(AppState state)
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public static class TreeRepository
{
    const string Url = "https://raw.githubusercontent.com/grindinggear/skilltree-export/master/data.json";

    public static string CachePath => Path.Combine(StateStore.Dir, "tree", "data.json");

    public static async Task<PassiveTree> LoadAsync(HttpClient http, bool forceDownload = false, CancellationToken ct = default)
    {
        if (forceDownload || !File.Exists(CachePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var bytes = await http.GetByteArrayAsync(Url, ct);
            var tmp = CachePath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, CachePath, overwrite: true);
        }
        return await Task.Run(() =>
        {
            using var fs = File.OpenRead(CachePath);
            return PassiveTree.Load(fs);
        }, ct);
    }
}

/// <summary>A build in the recent list: its PoB XML (a chain of saved stages exists nowhere else), where it came from, how far it went.</summary>
public sealed class RecentBuild
{
    public string Id { get; set; } = "";
    /// <summary>"Ranger · Pathfinder, ур. 92".</summary>
    public string Title { get; set; } = "";
    /// <summary>The link it was loaded from; empty for a pasted code.</summary>
    public string Input { get; set; } = "";
    public string Xml { get; set; } = "";
    public int StageIndex { get; set; }
    public int Stages { get; set; }
    public DateTime At { get; set; }
}
