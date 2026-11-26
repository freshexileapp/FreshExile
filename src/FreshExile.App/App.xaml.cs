using System.Net.Http;
using System.Windows;
using FreshExile.Core;

namespace FreshExile.App;

public partial class App : Application
{
    /// <summary>
    /// The panel's close button ends the program: a log tail, the tree watcher or a pending request
    /// must not keep a windowless process alive (it also locks the exe against a rebuild).
    /// </summary>
    /// <summary>Set by the language switch: once the panel has closed and saved, the exe starts again.</summary>
    public static bool RestartOnExit { get; set; }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        if (Updater.ApplyPending()) Log.Write("update: installed on exit");
        if (RestartOnExit && Environment.ProcessPath is { } exe)
            System.Diagnostics.Process.Start(exe);
        Environment.Exit(e.ApplicationExitCode);
    }

    async Task UpdateNowAsync(HttpClient http)
    {
        try
        {
            Updater.CleanUp();
            var latest = await UpdateCheck.LatestAsync(http);
            if (latest == null || !UpdateCheck.IsNewer(latest, Updater.Current))
                Log.Write($"update: {Updater.Current} is the latest");
            else if (await Updater.DownloadAsync(latest) is { } error)
                Log.Write($"update: {latest.Version} not installed: {error}");
        }
        catch (Exception ex) { Log.Write("update: " + ex.Message); }
        finally { Shutdown(); }
    }

    /// <summary>
    /// The titles' font is embedded (Fonts\AlegreyaSC-*.ttf); if WPF cannot find both faces, everything falls back to
    /// Segoe UI silently. Say so in overlay.log, so a broken resource shows up in a report.
    /// </summary>
    static void CheckDisplayFont()
    {
        try
        {
            var faces = System.Windows.Media.Fonts.GetTypefaces(new Uri("pack://application:,,,/"), "./Fonts/")
                .Where(t => t.FontFamily.FamilyNames.Values.Any(n => n.StartsWith("Alegreya SC", StringComparison.Ordinal)))
                .Select(t => t.Weight.ToOpenTypeWeight()).Distinct().ToList();
            if (!faces.Contains(500) || !faces.Contains(700))
                Log.Write($"warning: display font Alegreya SC not found in the resources (weights: {string.Join(",", faces)}); titles fall back to Segoe UI");
        }
        catch (Exception ex) { Log.Write("warning: display font check failed: " + ex.Message); }
    }

    async Task ShotMercAsync(MainViewModel vm, string file, string png, bool search)
    {
        try
        {
            var frame = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(System.IO.Path.GetFullPath(file))), System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var px = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            frame.CopyPixels(px, frame.PixelWidth * 4, 0);
            var crop = MercScan.Crop(px, frame.PixelWidth, frame.PixelHeight, new Int32Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
            var (check, error) = await vm.PrepareMercenaryCheckAsync(crop);
            if (check == null) { Log.Write("shot-merc: " + error); return; }
            if (search && check.CanRequest) await check.SearchAsync();
            UiShots.SaveOffScreen(new PriceCheckWindow { DataContext = check }, png);
        }
        catch (Exception ex) { Log.Write("shot-merc failed: " + ex.Message); }
        finally { Shutdown(); }
    }

    async Task ShotPriceAsync(MainViewModel vm, string text, string png, bool search)
    {
        try
        {
            var (check, error) = await vm.PreparePriceCheckAsync(text);
            if (check == null) { Log.Write("shot-price: " + error); return; }
            if (search && check.CanRequest) await check.SearchAsync();
            UiShots.SaveOffScreen(new PriceCheckWindow { DataContext = check }, png);
        }
        catch (Exception ex) { Log.Write("shot-price failed: " + ex.Message); }
        finally { Shutdown(); }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // a version downloaded before a crash (no clean exit to install it): put it in place and start it instead
        if (Updater.ApplyPending() && Environment.ProcessPath is { } self)
        {
            System.Diagnostics.Process.Start(self, e.Args);
            Environment.Exit(0);
        }
        MainViewModel? vmRef = null;
        CrashGuard.Install(this, () => vmRef);

        // "FreshExile.exe --report [--with-frames]": zip to the desktop without opening the overlay
        if (e.Args.Contains("--report"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var path = Report.Create(StateStore.Load(), includeFrames: e.Args.Contains("--with-frames"));
            Log.Write("report saved: " + Privacy.Scrub(path));
            Shutdown();
            return;
        }

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FreshExile/0.1");
        TreeArt.Init(http);

        // "FreshExile.exe --update": install the latest release without opening the overlay (scripts, testing the updater)
        if (e.Args.Contains("--update"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = UpdateNowAsync(http);
            return;
        }

        CheckDisplayFont();
        var state = StateStore.Load();
        state.Language ??= Loc.FromSystem();
        Loc.Use(state.Language);
        UiScale.Set(state.UiScale);

        // QA: "--shot-map-regex=<png>" draws the map regex window off screen and quits: no overlay over a running game
        if (e.Args.FirstOrDefault(a => a.StartsWith("--shot-map-regex=")) is { } regexShot)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UiShots.SaveOffScreen(new MapRegexWindow(new MapRegexViewModel(state.MapRegex, () => { })), regexShot["--shot-map-regex=".Length..]);
            Shutdown();
            return;
        }

        var vm = new MainViewModel(state, http);
        vmRef = vm;

        // QA: "--shot-merc=<window.png>=<png>": a picture of the mercenary window read as the screen would be, priced,
        // the price window drawn off screen, then quit
        if (e.Args.FirstOrDefault(a => a.StartsWith("--shot-merc=")) is { } mercShot && mercShot.Split('=', 3) is [_, var mercFile, var mercPng])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = ShotMercAsync(vm, mercFile, mercPng, e.Args.Contains("--shot-search"));
            return;
        }

        // QA: "--shot-price=<item.txt>=<png>": the price check window on that item text, drawn off screen (with the
        // search done, when "--shot-search" is given too), then quit: nothing over a running game
        if (e.Args.FirstOrDefault(a => a.StartsWith("--shot-price=")) is { } priceShot && priceShot.Split('=', 3) is [_, var itemFile, var png])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = ShotPriceAsync(vm, System.IO.File.ReadAllText(itemFile), png, e.Args.Contains("--shot-search"));
            return;
        }
        var main = new MainWindow(vm);
        MainWindow = main;
        main.Show();
        vm.Start();
        main.RestoreCompact();

        if (e.Args.Contains("--demo-town"))
        {
            var town = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            town.Tick += (_, _) => { town.Stop(); vm.DemoTown(); };
            town.Start();
        }

        if (e.Args.Contains("--demo-swap"))
        {
            // gem data loads async: give it a moment so the "buy" lines are filled
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) => { t.Stop(); vm.DemoAnnounce(); };
            t.Start();
        }

        // "--demo-price=<file>": the item text in the file (as Ctrl+Alt+C copies it) instead of the sample boots
        if (e.Args.FirstOrDefault(a => a.StartsWith("--demo-price")) is { } demo)
        {
            var item = demo.Split('=', 2) is [_, var file] ? System.IO.File.ReadAllText(file) : DemoItems.Boots;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) => { t.Stop(); _ = main.DemoPriceCheckAsync(item); };
            t.Start();
        }

        // QA: "--settings" (or "--settings=keys") opens the settings window on a section
        if (e.Args.FirstOrDefault(a => a.StartsWith("--settings")) is { } open)
        {
            main.OpenSettings();
            if (open.Split('=') is [_, var section]) vm.OpenSettingsSection(section);
        }

        // QA: "--ui-shots=<folder>" saves every open window as a PNG a few seconds in (the window itself, drawn by WPF:
        // nothing behind it, no game, no desktop), to look at a layout change without a screen capture
        if (e.Args.FirstOrDefault(a => a.StartsWith("--ui-shots=")) is { } shots)
        {
            var dir = shots["--ui-shots=".Length..];
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            t.Tick += (_, _) => { t.Stop(); UiShots.SaveAll(dir, vm); };
            t.Start();
        }

        if (e.Args.Contains("--demo-newchar"))
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) => { t.Stop(); vm.DemoNewCharacter(); };
            t.Start();
        }
    }
}

/// <summary>QA sample: rare boots as Ctrl+Alt+C copies them.</summary>
static class DemoItems
{
    public const string Boots = """
        Item Class: Boots
        Rarity: Rare
        Ghoul Stride
        Hydrascale Boots
        --------
        Quality: +10% (augmented)
        Armour: 132 (augmented)
        Evasion Rating: 132 (augmented)
        --------
        Requirements:
        Level: 59
        --------
        Sockets: R-R-G B
        --------
        Item Level: 75
        --------
        { Prefix Modifier "Agile" (Tier: 3) — Attribute }
        +40(38-42) to Dexterity
        { Suffix Modifier "of the Cheetah" (Tier: 3) — Speed }
        25(25-29)% increased Movement Speed
        { Suffix Modifier "of Ephij" (Tier: 7) — Elemental, Lightning, Resistance }
        +34(30-35)% to Lightning Resistance
        { Suffix Modifier "of the Kiln" (Tier: 7) — Elemental, Fire, Resistance }
        +37(36-41)% to Fire Resistance
        { Prefix Modifier "Hale" (Tier: 6) — Life }
        +45(40-49) to maximum Life
        """;
}
