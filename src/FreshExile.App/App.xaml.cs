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

        var state = StateStore.Load();
        state.Language ??= Loc.FromSystem();
        Loc.Use(state.Language);
        UiScale.Set(state.UiScale);

        var vm = new MainViewModel(state, http);
        vmRef = vm;
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

        if (e.Args.Contains("--demo-price"))
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) => { t.Stop(); _ = main.DemoPriceCheckAsync(DemoItems.Boots); };
            t.Start();
        }

        // QA: "--settings" (or "--settings=keys") opens the settings window on a section
        if (e.Args.FirstOrDefault(a => a.StartsWith("--settings")) is { } open)
        {
            main.OpenSettings();
            if (open.Split('=') is [_, var section]) vm.OpenSettingsSection(section);
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
