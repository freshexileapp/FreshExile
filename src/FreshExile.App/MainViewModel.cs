using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using FreshExile.Core;

namespace FreshExile.App;

public sealed class StagePip(int index, string title) : Observable
{
    bool _current, _past;
    string _title = title;
    public int Index { get; } = index;
    /// <summary>The player's name for the stage when given, else PoB's.</summary>
    public string Title { get => _title; set => Set(ref _title, value); }
    public bool IsCurrent { get => _current; set => Set(ref _current, value); }
    public bool IsPast { get => _past; set => Set(ref _past, value); }
}

public sealed class GemRowVm(Gem gem, bool isNew, bool inCharacter, Action<GemRowVm> onChanged) : Observable
{
    bool _bought, _visible = true;
    public Gem Gem { get; } = gem;
    public string Name => Gem.Name;
    public bool IsSupport => Gem.IsSupport;
    public string LevelText => $"lv {Gem.Level}";
    public bool IsNew { get; } = isNew;
    public bool InCharacter { get; } = inCharacter;

    public string? SourceText { get; init; }
    public string? SourceTip { get; init; }
    public bool HasSource => SourceText != null;
    public GemSource? From { get; init; }
    public string Npc => From?.Npc ?? "";

    /// <summary>Transfigured: made at the Divine Font in the Labyrinth, no vendor sells it.</summary>
    public bool FromLab { get; init; }

    /// <summary>"А3 · Clarissa" for the town list: not every vendor is remembered by name.</summary>
    public string NpcWithAct => FromLab ? Loc.T("Лаба") : From == null ? "" : Loc.T($"А{From.Act} · {From.Npc}");
    public GemColour Colour { get; init; }
    public System.Windows.Media.Brush OrbStroke => GemPalette.Stroke(Colour);
    public System.Windows.Media.Brush OrbFill => GemPalette.Fill(Colour);

    public bool IsBought
    {
        get => _bought;
        set { if (Set(ref _bought, value)) onChanged(this); }
    }

    public void SetBoughtSilently(bool value) => Set(ref _bought, value, nameof(IsBought));
    public bool IsVisible { get => _visible; set => Set(ref _visible, value); }
}

public sealed class GroupVm(string label, IReadOnlyList<GemRowVm> rows) : Observable
{
    bool _visible = true;
    public string Label { get; } = label;

    /// <summary>PoB slot of the link ("Boots"): the item there may add supports of its own.</summary>
    public string Slot { get; init; } = "";
    /// <summary>Switched off in PoB: only the links view shows them, dimmed.</summary>
    public IReadOnlyList<Gem> Disabled { get; init; } = [];
    public bool HasLabel => Label.Length > 0;

    /// <summary>In the second weapon set ("Weapon 1 Swap"): gems levelled there for later, not the build itself.</summary>
    public bool IsSwap => Slot.EndsWith(" Swap", StringComparison.Ordinal);
    public static readonly string SwapText = Loc.T("⇄ 2-й набор оружия — качаются, не в билде");
    public string? SwapTip => IsSwap
        ? Loc.T("В PoB эта связка стоит во втором наборе оружия (свап): там гемы просто набирают уровень, в билде они не работают. Свап переключается клавишей X.")
        : null;
    public IReadOnlyList<GemRowVm> Rows { get; } = rows;
    public bool IsHeader => Rows.Count == 0;
    public bool IsCard => Rows.Count > 0;
    public bool IsVisible { get => _visible; set => Set(ref _visible, value); }
}

public sealed record KeyNodeVm(string Step, string Name, string Detail);

/// <summary>
/// One kind of the stage's choices on the tree, poe.ninja style: "Staff Mastery · 2" with the effects taken, or a
/// tattoo / runegraft with how many and what it grants.
/// </summary>
public sealed record TreePickVm(string Name, int Count, IReadOnlyList<string> Lines)
{
    public bool HasCount => Count > 1 || IsMastery;
    public bool IsMastery { get; init; }
    /// <summary>"У персонажа другой эффект: …": the character took this mastery with another effect than the build's.</summary>
    public IReadOnlyList<string> Clashes { get; init; } = [];
    public bool HasClashes => Clashes.Count > 0;
    /// <summary>A runegraft or a tattoo is an item to buy: a price check straight from its card.</summary>
    public bool CanBuy => !IsMastery;
}

/// <summary>One numbered node of the stage; IsNext = the very first point to spend.</summary>
public sealed record PlanStepVm(string Step, string Name, string Effect, bool IsKey, int NodeId, bool IsNext);

public sealed partial class MainViewModel : Observable
{
    enum Tab { Gems, Links, Tree, Gear }

    readonly AppState _s;
    readonly HttpClient _http;
    readonly PoeApi _api;
    readonly DispatcherTimer _toastTimer;
    readonly DispatcherTimer _saveTimer;
    ClientLogWatcher? _log;
    CharacterSnapshot? _char;
    Tab _tab = Tab.Gems;
    bool _compact, _busy, _treeLoading, _toastVisible, _treeClickThrough;
    string? _buildError, _charError, _charStatus, _treeStatus, _logStatus, _toast;
    string _buildInput;
    int _gameLevel;
    string? _gameZone;

    public MainViewModel(AppState state, HttpClient http)
    {
        _s = state;
        _http = http;
        _api = new PoeApi(http);
        _buildInput = state.BuildInput;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); IsToastVisible = false; };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        LoadBuildCommand = new RelayCommand(async () => await LoadBuildAsync(), _ => !IsBusy);
        SaveStageCommand = new RelayCommand(async () => await SaveStageAsync(), _ => !IsBusy);
        PrevCommand = new RelayCommand(Prev, _ => Build != null && StageIndex > 0);
        NextCommand = new RelayCommand(Next, _ => Build != null && StageIndex < Build.Stages.Count - 1);
        SelectStageCommand = new RelayCommand(p => { if (p is StagePip pip) SelectStage(pip.Index); });
        SyncCharacterCommand = new RelayCommand(async () => await SyncCharacterAsync(), _ => !IsBusy);
        DetectLogCommand = new RelayCommand(DetectLog);
        RefreshTreeCommand = new RelayCommand(async () => await EnsureTreeAsync(force: true), _ => !_treeLoading);
        MarkAllCommand = new RelayCommand(MarkAll, _ => Build != null);
        LoadHotkeys();
    }

    public AppState State => _s;
    public event Action? PlanChanged;

    public RelayCommand LoadBuildCommand { get; }
    public RelayCommand SaveStageCommand { get; }
    public RelayCommand PrevCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand SelectStageCommand { get; }
    public RelayCommand SyncCharacterCommand { get; }
    public RelayCommand DetectLogCommand { get; }
    public RelayCommand RefreshTreeCommand { get; }
    public RelayCommand MarkAllCommand { get; }

    public void Start()
    {
        PoeApi.Trace = Log.Write;
        StartRunClock();
        StartUpdates();
        if (!string.IsNullOrEmpty(_s.BuildXml))
        {
            try { ApplyBuild(BuildParser.Parse(_s.BuildXml)); }
            catch (PobFormatException) { _s.BuildXml = null; }
            if (Build != null) _s.BuildId ??= Build.Id; // a state from before the key: its marks sit under the old id
            // the build loaded before the list existed joins it (marked as the open one)
            if (Build != null && !_s.RecentBuilds.Any(IsOpen)) RememberBuild();
        }
        RefreshRecent();
        StartPobCheck();
        if (string.IsNullOrEmpty(_s.ClientLogPath) && ClientLogParser.AutoDetect() is { } found)
        {
            _s.ClientLogPath = found;
            Raise(nameof(ClientLogPath));
        }
        StartLog();
        RaisePages();
        if (Account.Length > 0 && Character.Length > 0) _ = SyncCharacterAsync(quiet: true);
    }

    public void Shutdown()
    {
        _log?.Dispose();
        SaveNow();
    }

    #region Build & stages

    public Build? Build { get; private set; }
    /// <summary>The stage's tree: GGG's export with the stage's cluster jewels grown into it (<see cref="PassiveTree.WithClusters"/>).</summary>
    public PassiveTree? Tree { get; private set; }

    /// <summary>GGG's export as loaded, shared by every stage.</summary>
    PassiveTree? _baseTree;
    PassiveTree? _stageTreeBase;
    string? _stageTreeKey;

    /// <summary>The stage tree for the current stage; the same tree object while its cluster jewels stay the same.</summary>
    void UpdateStageTree()
    {
        if (_baseTree == null) { Tree = null; return; }
        var jewels = Build != null && CurrentStage is { } stage ? Build.JewelsFor(stage) : [];
        var key = string.Join(",", jewels.Where(j => ClusterJewel.IsCluster(j.Item)).Select(j => $"{j.Socket}:{j.Item.GetHashCode()}"));
        if (ReferenceEquals(_stageTreeBase, _baseTree) && key == _stageTreeKey && Tree != null) return;
        _stageTreeBase = _baseTree;
        _stageTreeKey = key;
        Tree = _baseTree.WithClusters(jewels);
    }
    public PlanResult? Plan { get; private set; }

    /// <summary>The current stage's tree is all taken: Plan walks this later stage's path instead.</summary>
    public int? AheadStage { get; private set; }

    /// <summary>Caption over the numbered steps: says when they already belong to the next stage.</summary>
    public string StepsCaption => AheadStage is int a
        ? Loc.T($"ЭТАП СОБРАН · ДАЛЬШЕ «{StageName(a).ToUpperInvariant()}»")
        : Loc.T("ЧТО ДАЮТ УЗЛЫ · НАВЕДИ — ПОКАЖУ НА ДЕРЕВЕ");

    /// <summary>The current stage's jewels by the socket they go in.</summary>
    public IReadOnlyDictionary<int, PobItem> StageJewels =>
        Build != null && CurrentStage is { } stage
            ? Build.JewelsFor(stage).GroupBy(j => j.Socket).ToDictionary(g => g.Key, g => g.First().Item)
            : new Dictionary<int, PobItem>();
    public ObservableCollection<StagePip> Stages { get; } = new();
    public ObservableCollection<GroupVm> Groups { get; } = new();
    public ObservableCollection<KeyNodeVm> KeyNodes { get; } = new();
    public ObservableCollection<string> AscendancyNodes { get; } = new();

    public bool HasBuild => Build != null;
    public int StageIndex => _s.StageIndex;
    public Stage? CurrentStage => Build == null ? null : Build.Stages[Math.Clamp(_s.StageIndex, 0, Build.Stages.Count - 1)];
    public IReadOnlyDictionary<int, int>? CurrentMasteryEffects =>
        (AheadStage is int a && Build != null && a < Build.Stages.Count ? Build.Stages[a] : CurrentStage)?.Tree?.MasteryEffects;

    public string BuildLine => Build == null ? "Path of Exile"
        : string.IsNullOrEmpty(Build.Ascendancy) ? Build.ClassName : $"{Build.ClassName} · {Build.Ascendancy}";

    /// <summary>The stage's name as shown (the player's own when given).</summary>
    public string StageTitle => Build == null ? "" : StageName(StageIndex);
    public string StageNumberText => Build == null ? "" : Loc.T($"ЭТАП {StageIndex + 1} ИЗ {Build.Stages.Count}");
    /// <summary>"этап 4 из 14", quiet, beside the stage's name on the panel.</summary>
    public string StageOfText => Build == null ? "" : Loc.T($"этап {StageIndex + 1} из {Build.Stages.Count}");

    public string BuildInput
    {
        get => _buildInput;
        set { if (Set(ref _buildInput, value)) BuildError = null; }
    }

    public string? BuildError { get => _buildError; private set { Set(ref _buildError, value); Raise(nameof(HasBuildError)); } }
    public bool HasBuildError => !string.IsNullOrEmpty(_buildError);

    public bool IsBusy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) { Raise(nameof(LoadButtonText)); Raise(nameof(SaveStageText)); Raise(nameof(SyncButtonText)); CommandManagerInvalidate(); } }
    }

    System.Windows.Threading.DispatcherTimer? _pobCheck;

    /// <summary>At start and every 15 minutes: the PoB behind the open build's link, re-read.</summary>
    void StartPobCheck()
    {
        _ = RefreshFromLinkAsync();
        _pobCheck = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _pobCheck.Tick += (_, _) => _ = RefreshFromLinkAsync();
        _pobCheck.Start();
    }

    /// <summary>
    /// The build is kept as a copy: edited in PoB behind the same link, the copy went stale (a mastery taken out there
    /// still asked for here). A changed build is taken over, its key, marks and stage kept. Not a chain of saved stages:
    /// each of its stages is a snapshot of that link on purpose.
    /// </summary>
    async Task RefreshFromLinkAsync()
    {
        var input = _s.BuildInput.Trim();
        if (IsBusy || Build == null || !input.StartsWith("http", StringComparison.OrdinalIgnoreCase) || PobStages.IsChain(_s.BuildXml)) return;
        try
        {
            var code = await PobCode.ResolveCodeAsync(input, _http);
            var xml = await Task.Run(() => PobCode.DecodeToXml(code));
            // the user may have opened another build meanwhile
            if (xml == _s.BuildXml || IsBusy || _s.BuildInput.Trim() != input) return;
            var build = BuildParser.Parse(xml);
            if (build.Stages.Count == 0) return;
            _s.BuildXml = xml;
            _s.StageIndex = Math.Clamp(_s.StageIndex, 0, build.Stages.Count - 1);
            ApplyBuild(build);
            RaisePages();
            RememberBuild();
            Toast(Loc.T("Билд изменился в PoB по ссылке — обновил"));
            Save();
            Log.Write("pob: the build behind the link changed, reloaded");
        }
        catch (Exception ex) { Log.Write("pob check: " + ex.Message); }
    }

    public string LoadButtonText => IsBusy ? Loc.T("Загружаю…") : Loc.T("Загрузить билд");
    public string SaveStageText => IsBusy ? Loc.T("Загружаю…") : Loc.T("Сохранить как этап");

    async Task LoadBuildAsync()
    {
        if (string.IsNullOrWhiteSpace(BuildInput)) { BuildError = Loc.T("Вставь ссылку pobb.in или код Path of Building"); return; }
        IsBusy = true;
        BuildError = null;
        try
        {
            var code = await PobCode.ResolveCodeAsync(BuildInput, _http);
            var xml = await Task.Run(() => PobCode.DecodeToXml(code));
            var build = BuildParser.Parse(xml);
            if (build.Stages.Count == 0) throw new PobFormatException(Loc.T("В билде нет ни деревьев, ни наборов гемов"));

            var input = BuildInput.Trim();
            var (key, stage) = KnownBuild(xml, input);
            KeepStageOfCurrent();
            _s.BuildXml = xml;
            _s.BuildId = key ?? NewBuildKey();
            _s.BuildInput = input;
            _s.StageIndex = stage ?? 0;

            ApplyBuild(build);
            _tab = Tab.Gems;
            RaisePages();
            RememberBuild();
            Toast(Loc.T($"Билд загружен · {build.Stages.Count} этапов"));
            Save();
        }
        catch (PobFormatException ex) { BuildError = ex.Message; }
        catch (HttpRequestException ex) { BuildError = Loc.T($"Ссылка не открылась: {ex.Message}"); }
        catch (TaskCanceledException) { BuildError = Loc.T("Сайт не ответил, попробуй ещё раз"); }
        catch (Exception ex) { BuildError = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// "Сохранить как этап": the build behind the link right now (a streamer's live PoB keeps changing) becomes one more stage
    /// of our own copy. What is saved is the build itself, not the link: later changes there do not touch it.
    /// </summary>
    async Task SaveStageAsync()
    {
        if (string.IsNullOrWhiteSpace(BuildInput)) { BuildError = Loc.T("Вставь ссылку pobb.in или код Path of Building"); return; }
        IsBusy = true;
        BuildError = null;
        try
        {
            var code = await PobCode.ResolveCodeAsync(BuildInput, _http);
            var xml = await Task.Run(() => PobCode.DecodeToXml(code));
            BuildParser.Parse(xml); // a broken build fails here, before anything is replaced

            var baseXml = _s.BuildXml;
            // a one-stage build loaded the plain way (the same live PoB a while ago) is the first stage, not thrown away
            if (!PobStages.IsChain(baseXml) && Build is { Stages.Count: 1 } && baseXml != null)
                baseXml = PobStages.Append(null, baseXml).Xml;

            var (merged, outcome, title) = PobStages.Append(baseXml, xml);
            if (outcome == PobStages.Outcome.Unchanged) { Toast(Loc.T($"Ничего не поменялось с этапа «{title}»")); return; }

            var build = BuildParser.Parse(merged);
            // a grown chain is the same build: its key, and so its marks, stay; a new chain is a build of its own
            if (outcome != PobStages.Outcome.Appended || _s.BuildId == null) _s.BuildId = NewBuildKey();
            _s.BuildXml = merged;
            _s.BuildInput = BuildInput.Trim();
            _s.StageIndex = build.Stages.Count - 1;

            ApplyBuild(build);
            _tab = Tab.Gems;
            RaisePages();
            RememberBuild();
            Toast(outcome == PobStages.Outcome.Started ? Loc.T($"Новый билд из снимков · этап «{title}»") : Loc.T($"Сохранён этап {build.Stages.Count} · «{title}»"));
            Save();
        }
        catch (PobFormatException ex) { BuildError = ex.Message; }
        catch (HttpRequestException ex) { BuildError = Loc.T($"Ссылка не открылась: {ex.Message}"); }
        catch (TaskCanceledException) { BuildError = Loc.T("Сайт не ответил, попробуй ещё раз"); }
        catch (Exception ex) { BuildError = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// The key bought gems, done swaps and the user's stage levels / tiers / names are kept under: one per loaded build.
    /// Not <see cref="Build.Id"/> (class|ascendancy|stage titles): two different one-tree Pathfinder builds share that one,
    /// and their marks got mixed. Builds saved before keep their old key, so nothing is lost.
    /// </summary>
    string MarksKey => _s.BuildId ?? Build!.Id;

    static string NewBuildKey() => Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// The key (and stage) of a build loaded before: the same XML, or the same link (the PoB behind it may have changed,
    /// it is still that build) — the open one first, then the recent list. Null: a new build.
    /// </summary>
    (string? Key, int? Stage) KnownBuild(string xml, string input)
    {
        var link = input.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        bool Same(string? x, string? i) => x == xml || (link && string.Equals(i?.Trim(), input, StringComparison.OrdinalIgnoreCase));
        if (_s.BuildId != null && Same(_s.BuildXml, _s.BuildInput)) return (_s.BuildId, _s.StageIndex);
        return _s.RecentBuilds.FirstOrDefault(r => Same(r.Xml, r.Input)) is { } r ? (r.Id, r.StageIndex) : (null, null);
    }

    void ApplyBuild(Build build)
    {
        Build = build;
        _s.StageIndex = Math.Clamp(_s.StageIndex, 0, build.Stages.Count - 1);
        Stages.Clear();
        for (var i = 0; i < build.Stages.Count; i++) Stages.Add(new StagePip(i, StageName(i)));
        Raise(nameof(Build));
        Raise(nameof(HasBuild));
        Raise(nameof(BuildLine));
        RebuildStageLevels();
        RefreshStage();
        _ = EnsureTreeAsync();
        _ = EnsureGemsAsync();
    }

    void Prev() => SelectStage(StageIndex - 1);
    void Next() => SelectStage(StageIndex + 1);

    public void SelectStage(int index, string? toast = null)
    {
        if (Build == null) return;
        index = Math.Clamp(index, 0, Build.Stages.Count - 1);
        if (index == _s.StageIndex) return;
        _s.StageIndex = index;
        RefreshStage();
        Save();
        if (toast != null)
        {
            // the compact line already shows the new stage and the plate over the game tells the rest;
            // a toast there covers the town list and does not fit
            if (!IsCompact) Toast(HasSwap && !IsSwapDone ? Loc.T($"{toast} · поменяй связки") : toast);
            // toast is passed only when the game switched the stage: tell the player over the game too
            AnnounceSwap();
        }
    }

    public void StepStage(int delta) => SelectStage(StageIndex + delta);

    void RefreshStage()
    {
        if (Build == null) return;
        var idx = StageIndex;
        var stage = Build.Stages[idx];
        foreach (var p in Stages) { p.IsCurrent = p.Index == idx; p.IsPast = p.Index < idx; }

        var prevKeys = idx > 0 ? Build.Stages[idx - 1].AllGems.Select(g => g.Key).ToHashSet() : null;
        var bought = BoughtSet;
        Groups.Clear();
        foreach (var g in stage.Groups)
        {
            var rows = g.Gems.Select(gem =>
            {
                var (source, tip, colour, from) = DescribeGem(gem);
                var row = new GemRowVm(gem, prevKeys != null && !prevKeys.Contains(gem.Key), _char?.HasGem(gem) == true, OnRowChanged)
                {
                    SourceText = source,
                    SourceTip = tip,
                    Colour = colour,
                    From = from,
                    FromLab = _gems?.IsTransfigured(gem) == true,
                };
                row.SetBoughtSilently(bought.Contains(gem.Key));
                return row;
            }).ToList();
            Groups.Add(new GroupVm(g.Label, rows) { Slot = g.Slot, Disabled = g.Disabled });
        }

        RefreshLinks();
        RefreshGear();
        RaiseChoices();
        ComputeSwap();
        ApplyFilter();
        Recount();
        RefreshTownGems();
        Raise(nameof(StageIndex));
        Raise(nameof(StageTitle));
        Raise(nameof(StageNumberText));
        Raise(nameof(StageOfText));
        Raise(nameof(CurrentStage));
        Raise(nameof(HasNoGems));
        RecomputePlan();
        // without a synced character the previous stage stands in for the allocation
        if (_char == null) AlignNodesChanged?.Invoke();
        CommandManagerInvalidate();
    }

    #endregion

    #region Gems

    HashSet<string> BoughtSet
    {
        get
        {
            var id = MarksKey;
            if (!_s.Bought.TryGetValue(id, out var set)) _s.Bought[id] = set = new HashSet<string>();
            return set;
        }
    }

    public int TotalGems { get; private set; }
    public int BoughtGems { get; private set; }
    public double Progress => TotalGems == 0 ? 0 : (double)BoughtGems / TotalGems;
    public string ProgressText => TotalGems == 0 ? Loc.T("Нет гемов") : Loc.T($"Куплено {BoughtGems} из {TotalGems}");
    public string StageSubtitle { get; private set; } = "";
    /// <summary>The gems tab's badge: gems still to buy on this stage; empty = no badge.</summary>
    public string GemsBadge => TotalGems - BoughtGems is > 0 and var n ? n.ToString() : "";
    public bool HasGemsBadge => GemsBadge.Length > 0;
    public bool HasNoGems => CurrentStage != null && !CurrentStage.AllGems.Any();
    public bool ShowAllBought => HideBought && TotalGems > 0 && BoughtGems == TotalGems;

    public bool HideBought
    {
        get => _s.HideBought;
        set
        {
            if (_s.HideBought == value) return;
            _s.HideBought = value;
            Raise();
            ApplyFilter();
            Raise(nameof(ShowAllBought));
            Save();
        }
    }

    void OnRowChanged(GemRowVm row)
    {
        if (Build == null) return;
        var set = BoughtSet;
        if (row.IsBought) set.Add(row.Gem.Key); else set.Remove(row.Gem.Key);
        foreach (var r in Groups.SelectMany(g => g.Rows))
            if (r != row && r.Gem.Key == row.Gem.Key) r.SetBoughtSilently(row.IsBought);
        Recount();
        ApplyFilter();
        Save();
        // let the orb fill be seen before the gem leaves the town list
        _ = Task.Delay(450).ContinueWith(_ => RefreshTownGems(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    void MarkAll()
    {
        if (Build == null) return;
        var rows = Groups.SelectMany(g => g.Rows).ToList();
        var allBought = rows.All(r => r.IsBought);
        var set = BoughtSet;
        foreach (var r in rows)
        {
            r.SetBoughtSilently(!allBought);
            if (allBought) set.Remove(r.Gem.Key); else set.Add(r.Gem.Key);
        }
        Recount();
        ApplyFilter();
        Save();
    }

    void ApplyFilter()
    {
        foreach (var g in Groups)
        {
            // a bought main gem stays as the heading while its supports still have to be bought
            var supportsLeft = g.Rows.Any(r => r.IsSupport && !r.IsBought);
            foreach (var r in g.Rows) r.IsVisible = !HideBought || !r.IsBought || (!r.IsSupport && supportsLeft);
            g.IsVisible = g.IsHeader || g.Rows.Any(r => r.IsVisible);
        }
    }

    void Recount()
    {
        var rows = Groups.SelectMany(g => g.Rows).GroupBy(r => r.Gem.Key).Select(x => x.First()).ToList();
        TotalGems = rows.Count;
        BoughtGems = rows.Count(r => r.IsBought);
        var toBuy = TotalGems - BoughtGems;
        var fresh = rows.Count(r => r.IsNew && !r.IsBought);
        StageSubtitle = TotalGems == 0 ? Loc.T("Гемов нет") : toBuy == 0 ? Loc.T("Всё куплено") : fresh > 0 ? Loc.T($"{toBuy} к покупке · {fresh} новых") : Loc.T($"{toBuy} к покупке");

        Raise(nameof(TotalGems));
        Raise(nameof(BoughtGems));
        Raise(nameof(Progress));
        Raise(nameof(ProgressText));
        Raise(nameof(StageSubtitle));
        Raise(nameof(GemsBadge));
        Raise(nameof(HasGemsBadge));
        Raise(nameof(ShowAllBought));
    }

    #endregion

    #region Tree

    public string? TreeStatus { get => _treeStatus; private set { Set(ref _treeStatus, value); Raise(nameof(TreeHint)); Raise(nameof(HasTreeHint)); } }
    public int PointsToTake => Plan?.Order.Count ?? 0;
    public int RefundCount => Plan?.Refund.Count ?? 0;
    public int AscendancyCount => Plan?.Ascendancy.Count ?? 0;

    /// <summary>
    /// "Nature's Reprisal, Master Surgeon": ascendancy notables of this stage not taken yet (against the previous
    /// stage or the synced character) — what to pick at the next labyrinth.
    /// </summary>
    public string LabText => string.Join(", ", AscendancyNodes);
    public bool HasLabText => AscendancyNodes.Count > 0;
    public bool HasAscendancy => AscendancyCount > 0;
    public bool HasKeyNodes => KeyNodes.Count > 0;

    /// <summary>The stage's masteries, one per kind: how many of that kind it takes and every effect chosen.</summary>
    public ObservableCollection<TreePickVm> MasteryPicks { get; } = new();
    /// <summary>Runegrafts and tattoos PoB put on the stage's tree, one line per kind.</summary>
    public ObservableCollection<TreePickVm> RunegraftPicks { get; } = new();
    public ObservableCollection<TreePickVm> TattooPicks { get; } = new();
    public bool HasMasteryPicks => MasteryPicks.Count > 0;
    public bool HasRunegraftPicks => RunegraftPicks.Count > 0;
    public bool HasTattooPicks => TattooPicks.Count > 0;

    /// <summary>
    /// Masteries by kind ("Staff Mastery · 2", the effects chosen under it, in the tree's own words), tattoos and runegrafts
    /// by name (how many, what they grant): out of the stage's own PoB tree.
    /// </summary>
    void RefreshTreePicks()
    {
        MasteryPicks.Clear();
        RunegraftPicks.Clear();
        TattooPicks.Clear();
        if (CurrentStage?.Tree is { } spec && Tree != null)
        {
            var taken = spec.Nodes.ToHashSet();
            var masteries = spec.MasteryEffects
                .Where(kv => taken.Contains(kv.Key) && Tree.Nodes.ContainsKey(kv.Key) && !spec.Overrides.Any(o => o.NodeId == kv.Key))
                .Select(kv => (Node: Tree.Nodes[kv.Key], Effect: kv.Value))
                .GroupBy(x => x.Node.Name)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in masteries)
            {
                // each effect a line of its own; an effect of two stats keeps them together on its line
                var lines = g.Select(x => x.Node.MasteryEffects.TryGetValue(x.Effect, out var stats) ? string.Join(" · ", stats) : null)
                    .OfType<string>().Distinct().ToList();
                // compared with the character: a mastery it took with another effect than the build's, in red
                var clashes = g.Select(x => MasteryClash(x.Node, x.Effect)).OfType<string>().Distinct().ToList();
                MasteryPicks.Add(new TreePickVm(g.Key, g.Count(), lines) { IsMastery = true, Clashes = clashes });
            }
            foreach (var g in spec.Overrides.Where(o => taken.Contains(o.NodeId) || !Tree.Nodes.ContainsKey(o.NodeId))
                         .GroupBy(o => o.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var pick = new TreePickVm(g.Key, g.Count(), g.First().Lines) { Clashes = g.Select(OverrideClash).OfType<string>().Distinct().ToList() };
                if (g.First().IsRunegraft) RunegraftPicks.Add(pick);
                else TattooPicks.Add(pick);
            }
        }
        foreach (var n in new[] { nameof(HasMasteryPicks), nameof(HasRunegraftPicks), nameof(HasTattooPicks) }) Raise(n);
    }

    public string? TreeHint =>
        TreeStatus ?? (CurrentStage is { Tree: null } ? Loc.T("В PoB нет дерева для этого этапа") : null);
    public bool HasTreeHint => TreeHint != null;

    public bool CompareWithCharacter
    {
        get => _s.CompareWithCharacter;
        set
        {
            if (_s.CompareWithCharacter == value) return;
            _s.CompareWithCharacter = value;
            Raise();
            Raise(nameof(CompareWithPrevious));
            Raise(nameof(CompareHint));
            RecomputePlan();
            Save();
        }
    }

    public bool CompareWithPrevious
    {
        get => !CompareWithCharacter;
        set => CompareWithCharacter = !value;
    }

    public string CompareHint => CompareWithCharacter && _char != null
        ? Loc.T($"Сравнение с {_char.Name} (ур. {_char.Level})")
        : _s.FreshCharacter ? Loc.T("Новый персонаж: путь от стартовой точки, взятое — по экрану игры")
        : CompareWithCharacter ? Loc.T("Персонаж не синхронизирован: настройки → Персонаж")
        : StageIndex == 0 ? Loc.T("Первый этап: путь от стартовой точки класса") : Loc.T("Считаем, что прошлый этап уже взят");

    /// <summary>
    /// What counts as taken without a synced character: the previous stage's tree — or, for a fresh character, nothing
    /// (what the overlay saw gold is added by <see cref="Live"/>).
    /// </summary>
    IEnumerable<int> AssumedTaken() => _s.FreshCharacter ? [] : PreviousTreeNodes();

    public bool TreeClickThrough
    {
        get => _treeClickThrough;
        set => Set(ref _treeClickThrough, value);
    }

    public double TreeBgOpacity
    {
        get => _s.TreeBgOpacity;
        set { _s.TreeBgOpacity = Math.Clamp(value, 0, 1); Raise(); Save(); }
    }

    async Task EnsureTreeAsync(bool force = false)
    {
        if ((_baseTree != null && !force) || _treeLoading) return;
        _treeLoading = true;
        TreeStatus = force || !File.Exists(TreeRepository.CachePath) ? Loc.T("Скачиваю дерево пассивок (~7 МБ)…") : Loc.T("Загружаю дерево…");
        try
        {
            if (force) _ = EnsureGemsAsync(force: true);
            _baseTree = await TreeRepository.LoadAsync(_http, force);
            UpdateStageTree();
            TreeStatus = null;
            Raise(nameof(TreeInfo));
            RecomputePlan();
            RefreshGear(); // cluster jewels read the tree to tell which notables are taken
            AlignNodesChanged?.Invoke();
            if (force) Toast(Loc.T("Данные дерева обновлены"));
        }
        catch (Exception ex) { TreeStatus = Loc.T($"Дерево не загрузилось: {ex.Message}"); }
        finally { _treeLoading = false; CommandManagerInvalidate(); }
    }

    public string TreeInfo =>
        (_baseTree == null ? Loc.T("Дерево ещё не загружено") : Loc.T($"Дерево: {_baseTree.Nodes.Count} узлов (GGG skilltree-export)")) + "\n" +
        (_gems == null ? GemStatus ?? Loc.T("Данные гемов ещё не загружены") : Loc.T($"Гемы и NPC: {_gems.Count} гемов (exile-leveling, MIT)"));

    void RecomputePlan()
    {
        // a stage of its own may socket other cluster jewels: their passives join the tree before the path is laid
        UpdateStageTree();
        var stage = CurrentStage;
        AheadStage = null;
        if (Tree == null || stage?.Tree == null || Build == null) Plan = null;
        else
        {
            IEnumerable<int> have = Live(CompareWithCharacter && _char != null ? _char.Nodes : AssumedTaken());
            var haveList = have.ToList();
            Plan = PathPlanner.Plan(Tree, haveList, stage.Tree.Nodes, stage.Tree.ClassId);
            // a node the next stage takes is a point spent early, not one to refund
            if (Plan.Refund.Count > 0 && Build.Stages.Skip(StageIndex + 1).FirstOrDefault(s => s.Tree != null)?.Tree is { } nextTree)
            {
                var later = nextTree.Nodes.ToHashSet();
                if (Plan.Refund.Any(later.Contains))
                    Plan = Plan with { Refund = Plan.Refund.Where(id => !later.Contains(id)).ToHashSet() };
            }
            // the stage's tree is done while the game still holds the guide on it (act stages wait for the next act,
            // levels outrun them): the points keep going to the next stage's path instead of nowhere
            if (Plan.Order.Count == 0 && Plan.Refund.Count == 0)
                for (var i = StageIndex + 1; i < Build.Stages.Count; i++)
                {
                    if (Build.Stages[i].Tree is not { } next) continue;
                    var ahead = PathPlanner.Plan(Tree, haveList, next.Nodes, next.ClassId);
                    if (ahead.Order.Count == 0) continue;
                    // only new points: a respec or the lab of a later stage waits for that stage
                    Plan = ahead with { Refund = new HashSet<int>(), Ascendancy = [] };
                    AheadStage = i;
                    break;
                }
        }
        Raise(nameof(StepsCaption));

        KeyNodes.Clear();
        AscendancyNodes.Clear();
        if (Plan != null && Tree != null)
        {
            for (var i = 0; i < Plan.Order.Count; i++)
            {
                var n = Tree.Nodes[Plan.Order[i]];
                if (!n.IsKey) continue;
                KeyNodes.Add(new KeyNodeVm($"{i + 1}", NodeName(n), NodeDetail(n)));
            }
            foreach (var id in Plan.Ascendancy)
                if (Tree.Nodes[id].Kind == NodeKind.Notable) AscendancyNodes.Add(Tree.Nodes[id].Name);
        }

        Raise(nameof(Plan));
        Raise(nameof(PointsToTake));
        Raise(nameof(RefundCount));
        Raise(nameof(AscendancyCount));
        Raise(nameof(HasAscendancy));
        Raise(nameof(LabText));
        Raise(nameof(HasLabText));
        Raise(nameof(HasKeyNodes));
        Raise(nameof(TreeHint));
        Raise(nameof(HasTreeHint));
        Raise(nameof(CompareHint));
        Raise(nameof(CurrentMasteryEffects));
        RefreshTreePicks();
        RebuildPlanSteps();
        PlanChanged?.Invoke();
    }

    IEnumerable<int> PreviousTreeNodes()
    {
        for (var i = StageIndex - 1; i >= 0; i--)
            if (Build!.Stages[i].Tree is { } t) return t.Nodes;
        return [];
    }

    public string NodeDetail(PassiveNode n) => n.Kind switch
    {
        _ when OverrideOf(n.Id) is { } o => string.Join("; ", o.Lines),
        NodeKind.Mastery => MasteryText(n) ?? Loc.T("Мастерство"),
        NodeKind.Keystone => "Keystone",
        NodeKind.Jewel => Loc.T("Гнездо самоцвета"),
        NodeKind.Notable => "Notable",
        _ => Loc.T("Пассивка"),
    };

    /// <summary>
    /// "У персонажа другой эффект: +30 to Strength": the character, compared with, took the mastery with another effect than
    /// the build's (the game lets you change it there); null when it is the same, not taken or not compared with.
    /// </summary>
    public string? MasteryClash(PassiveNode n, int buildEffect)
    {
        if (!CompareWithCharacter || _char == null || OverrideOf(n.Id) != null
            || !_char.MasteryEffects.TryGetValue(n.Id, out var theirs) || theirs == buildEffect) return null;
        var effect = n.MasteryEffects.TryGetValue(theirs, out var stats) ? string.Join(" · ", stats) : "?";
        return Loc.T($"У персонажа другой эффект: {effect}");
    }

    /// <summary>"Купить" on a runegraft / tattoo card: the price check for it by name (the bulk exchange sells them).</summary>
    public event Action<string>? NamedPriceRequested;
    public RelayCommand BuyPickCommand => _buyPick ??= new RelayCommand(p => { if (p is TreePickVm { CanBuy: true } pick) NamedPriceRequested?.Invoke(pick.Name); });
    RelayCommand? _buyPick;

    /// <summary>
    /// Nodes the character holds differently from the build: a mastery with another effect, or a node taken without the
    /// runegraft / tattoo the build puts on it. Marked on the trees with a red dashed ring.
    /// </summary>
    public IReadOnlySet<int> MasteryClashNodes => CurrentStage?.Tree is { } spec && Tree != null
        ? spec.MasteryEffects.Where(kv => OverrideOf(kv.Key) == null && Tree.Nodes.TryGetValue(kv.Key, out var n) && MasteryClash(n, kv.Value) != null)
            .Select(kv => kv.Key)
            .Concat(spec.Overrides.Where(o => OverrideClash(o) != null).Select(o => o.NodeId))
            .ToHashSet()
        : new HashSet<int>();

    /// <summary>
    /// "У персонажа здесь не руна: …": the character has the node taken, but not with the build's runegraft / tattoo
    /// (a plain mastery or passive, or another one); null when it is the same, not taken yet or not compared with.
    /// </summary>
    public string? OverrideClash(TreeOverride o)
    {
        if (!CompareWithCharacter || _char == null || !_char.Nodes.Contains(o.NodeId)) return null;
        if (_char.Overrides.TryGetValue(o.NodeId, out var theirs) && string.Equals(theirs, o.Name, StringComparison.OrdinalIgnoreCase)) return null;
        return string.IsNullOrEmpty(theirs) ? Loc.T("У персонажа узел взят без неё") : Loc.T($"У персонажа здесь другое: {theirs}");
    }

    /// <summary>
    /// The runegraft / tattoo PoB put on the node, if any. It replaces the node: a mastery under a runegraft is no mastery
    /// in the game any more (PoB still keeps its old effect in masteryEffects).
    /// </summary>
    public TreeOverride? OverrideOf(int nodeId) =>
        (CurrentStage?.Tree is { } t ? t : null)?.Overrides.FirstOrDefault(o => o.NodeId == nodeId);

    /// <summary>The node's name as the game shows it: the runegraft's / tattoo's when one replaces it.</summary>
    public string NodeName(PassiveNode n) => OverrideOf(n.Id)?.Name ?? n.Name;

    /// <summary>Passives replaced by runegrafts and tattoos on the stage's tree: marked on the trees.</summary>
    public IReadOnlySet<int> OverrideNodes => CurrentStage?.Tree?.Overrides.Select(o => o.NodeId).ToHashSet() ?? new HashSet<int>();
    /// <summary>The tattoos among them: their ring has a colour of its own.</summary>
    public IReadOnlySet<int> TattooNodes => CurrentStage?.Tree?.Overrides.Where(o => o.IsTattoo).Select(o => o.NodeId).ToHashSet() ?? new HashSet<int>();

    /// <summary>The build's effect of the mastery; when the character took it with another one, that is said on a second line.</summary>
    public string? MasteryText(PassiveNode n) =>
        OverrideOf(n.Id) is { } o ? string.Join("; ", o.Lines)
        : CurrentMasteryEffects is { } me && me.TryGetValue(n.Id, out var eff) && n.MasteryEffects.TryGetValue(eff, out var stats)
            ? string.Join("; ", stats) + (MasteryClash(n, eff) is { } clash ? "\n" + clash : "")
            : null;

    #endregion

    #region Game log

    public string ClientLogPath
    {
        get => _s.ClientLogPath;
        set
        {
            value = value.Trim().Trim('"');
            if (_s.ClientLogPath == value) return;
            _s.ClientLogPath = value;
            Raise();
            StartLog();
            Save();
        }
    }

    /// <summary>Settings master switch, see <see cref="AppState.Tracking"/>.</summary>
    public bool Tracking
    {
        get => _s.Tracking;
        set
        {
            if (_s.Tracking == value) return;
            _s.Tracking = value;
            if (!value)
            {
                _newCharName = null;
                RaiseNewChar();
            }
            Raise();
            foreach (var n in new[] { nameof(ShowTownBlock) }) Raise(n);
            Save();
        }
    }

    public bool FollowGame
    {
        get => _s.FollowGame;
        set { if (_s.FollowGame == value) return; _s.FollowGame = value; Raise(); Save(); }
    }

    public string? LogStatus { get => _logStatus; private set => Set(ref _logStatus, value); }
    public bool IsGameLinked => _log != null;

    public string GameLine
    {
        get
        {
            if (_log == null) return Loc.T("Client.txt не найден: укажи путь в настройках");
            var parts = new List<string>();
            if (_gameLevel > 0) parts.Add(Loc.T($"Ур. {_gameLevel}"));
            if (!string.IsNullOrEmpty(_gameZone)) parts.Add(_gameZone);
            return parts.Count == 0 ? Loc.T("Жду событий из игры…") : string.Join(" · ", parts);
        }
    }

    void DetectLog()
    {
        var p = ClientLogParser.AutoDetect();
        if (p == null) LogStatus = Loc.T("Не нашёл автоматически. Укажи файл через «Обзор…»");
        else ClientLogPath = p;
    }

    void StartLog()
    {
        _log?.Dispose();
        _log = null;
        var path = _s.ClientLogPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(ClientLogParser.ResolveLive(path)))
        {
            LogStatus = string.IsNullOrEmpty(path) ? Loc.T("Путь не задан") : Loc.T("Файл не найден");
        }
        else
        {
            try
            {
                var w = new ClientLogWatcher(path);
                var d = Application.Current.Dispatcher;
                w.Event += e => d.BeginInvoke(() => OnLog(e));
                w.Start();
                _log = w;
                LogStatus = Loc.T($"Подключено · читаю {Path.GetFileName(w.Path)}");
            }
            catch (Exception ex) { LogStatus = Loc.T($"Не читается: {ex.Message}"); }
        }
        Raise(nameof(IsGameLinked));
        Raise(nameof(GameLine));
    }

    /// <summary>Game log → act, level, zone, town and forward-only stage switches (logic lives in Core).</summary>
    readonly StageFollower _follower = new([]);

    void OnLog(LogEvent e)
    {
        // the tail read at start: only the act / map timer replays it
        if (e is AreaHistory h)
        {
            _runTimer.Replay(h.Areas, h.LastLine, DateTime.Now);
            RaiseRunTimer();
            return;
        }
        // before the name is filled in below: it is what the event is compared with
        // tracking off: the log still tells the level (the price check's "wear it now"), nothing acts on it
        // on the first stage too, when the old character left "bought" marks or a synced profile behind
        if (Tracking && Build != null && NewCharacter.Detect(e, Character, StageIndex, BoughtSet.Count > 0 || _char != null) is { } who)
            OfferNewCharacter(who);
        var sw = Build == null ? null : _follower.OnEvent(e, StageIndex, FollowGame && Tracking);
        _gameLevel = _follower.Level;
        _gameZone = _follower.Zone;
        switch (e)
        {
            case LevelUp l:
                _lastLevel = l.Level;
                // "с N ур." hints depend on the current level
                if (_gems != null && sw == null) RefreshStage();
                if (string.IsNullOrEmpty(Character)) Character = l.Character;
                break;
            case AreaGenerated a:
                OnArea(a);
                break;
        }
        if (sw != null) SelectStage(sw.Stage, sw.Reason);
        Raise(nameof(GameLine));
        Raise(nameof(MiniLevel));
    }

    #endregion

    #region Character

    public string Account
    {
        get => _s.Account;
        set { if (_s.Account == value) return; _s.Account = value.Trim(); Raise(); Save(); }
    }

    public string Character
    {
        get => _s.Character;
        set { if (_s.Character == value) return; _s.Character = value.Trim(); Raise(); Save(); }
    }

    public string? CharStatus { get => _charStatus; private set => Set(ref _charStatus, value); }
    public string? CharError { get => _charError; private set { Set(ref _charError, value); Raise(nameof(HasCharError)); } }
    public bool HasCharError => !string.IsNullOrEmpty(_charError);
    public string SyncButtonText => IsBusy ? Loc.T("Загружаю…") : Loc.T("Синхронизировать");

    async Task SyncCharacterAsync(bool quiet = false)
    {
        IsBusy = true;
        CharError = null;
        try
        {
            _char = await _api.GetCharacterAsync(Account, Character);
            _lastSync = DateTime.UtcNow;
            ClearLive();
            // the profile now says what is taken: the fresh-character guesswork ends
            _s.FreshCharacter = false;
            Raise(nameof(CompareHint));
            // the real allocation is what the in-game overlay must continue from
            if (quiet && !_autoSynced) { _autoSynced = true; CompareWithCharacter = true; }
            AlignNodesChanged?.Invoke();
            if (Build != null)
            {
                var set = BoughtSet;
                foreach (var gem in Build.Stages.SelectMany(s => s.AllGems))
                    if (_char.HasGem(gem)) set.Add(gem.Key);
            }
            CharStatus = Loc.T($"{_char.Name} · ур. {_char.Level} · {_char.Nodes.Count} пассивок · {_char.GemKeys.Count} гемов");
            RefreshStage();
            if (Plan != null && Tree != null)
                Log.Write($"character sync: {_char.Nodes.Count} nodes; plan starts with "
                          + string.Join(", ", Plan.Order.Take(3).Select(id => $"{id} {Tree.Nodes[id].Name} ({(_char.Nodes.Contains(id) ? "in profile" : "not in profile")})")));
            Save();
            if (!quiet) Toast(Loc.T("Персонаж синхронизирован"));
        }
        catch (PoeApiException ex) { CharError = ex.Message; }
        catch (Exception ex) { CharError = Loc.T($"Нет связи с pathofexile.com: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    #endregion

    #region Pages, toast, save

    public bool IsGemsTab
    {
        get => _tab == Tab.Gems;
        set { if (value && _tab != Tab.Gems) { _tab = Tab.Gems; RaisePages(); } }
    }

    public bool IsLinksTab
    {
        get => _tab == Tab.Links;
        set { if (value && _tab != Tab.Links) { _tab = Tab.Links; RaisePages(); } }
    }

    public bool IsTreeTab
    {
        get => _tab == Tab.Tree;
        set { if (value && _tab != Tab.Tree) { _tab = Tab.Tree; RaisePages(); } }
    }

    public bool IsGearTab
    {
        get => _tab == Tab.Gear;
        set { if (value && _tab != Tab.Gear) { _tab = Tab.Gear; RaisePages(); } }
    }

    public bool IsCompact
    {
        get => _compact;
        set { if (Set(ref _compact, value)) RaisePages(); }
    }

    // settings live in their own window (SettingsWindow): the panel is the stage or the empty state
    public bool ShowEmpty => Build == null && !IsCompact;
    public bool ShowStage => Build != null;
    public bool ShowTabs => ShowStage && !IsCompact;
    public bool ShowGems => ShowTabs && _tab == Tab.Gems;
    public bool ShowLinks => ShowTabs && _tab == Tab.Links;
    public bool ShowTreeTab => ShowTabs && _tab == Tab.Tree;
    public bool ShowGear => ShowTabs && _tab == Tab.Gear;
    public bool NeedsFocus => ShowEmpty;

    void RaisePages()
    {
        foreach (var n in new[]
                 {
                     nameof(IsGemsTab), nameof(IsLinksTab), nameof(IsTreeTab), nameof(IsGearTab), nameof(IsCompact), nameof(ShowEmpty),
                     nameof(ShowStage), nameof(ShowTabs), nameof(ShowGems), nameof(ShowLinks), nameof(ShowTreeTab), nameof(ShowGear), nameof(NeedsFocus),
                 })
            Raise(n);
        RaiseMini();
    }

    public string? ToastText { get => _toast; private set => Set(ref _toast, value); }
    public bool IsToastVisible { get => _toastVisible; private set => Set(ref _toastVisible, value); }

    public void Toast(string text)
    {
        ToastText = text;
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void Save()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        try { StateStore.Save(_s); }
        catch (IOException) { /* next save will retry */ }
    }

    static void CommandManagerInvalidate() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    #endregion
}
