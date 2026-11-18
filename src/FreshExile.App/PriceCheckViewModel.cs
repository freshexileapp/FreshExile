using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Threading;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>One filter row: tick, the line, tier badge, min / max boxes.</summary>
public sealed class PriceFilterVm(PriceFilter f) : Observable
{
    static readonly Brush ModBlue = Frozen(0x88, 0x88, 0xFF);
    static readonly Brush CraftedBlue = Frozen(0xB4, 0xB4, 0xFF);
    static readonly Brush FracturedGold = Frozen(0xA2, 0x90, 0x60);
    static readonly Brush EnchantBlue = Frozen(0xB8, 0xDA, 0xF2);
    static readonly Brush Plain = Frozen(0xF4, 0xF4, 0xF5);

    static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    public PriceFilter Filter { get; } = f;

    bool _hidden;
    /// <summary>Folded away under "ещё N фильтров": rows not ticked when the window opened.</summary>
    public bool IsHidden { get => _hidden; set => Set(ref _hidden, value); }
    public string Label => Filter.Label;
    public bool Searchable => Filter.Searchable;
    public string? TierText => Filter.Tier is int t ? $"T{t}" : null;
    public bool HasTier => Filter.Tier != null;
    public string? RangeText => Filter.Range is { } r ? $"({r})" : null;
    public bool HasRange => Filter.Range != null;
    public bool IsTopTier => Filter.Tier is 1 or 2;
    public string KindText => Filter.ModKind switch
    {
        _ when Filter.IsPseudo || Filter.Kind != PriceFilterKind.Stat => "",
        ModKind.Implicit => Loc.T("имплисит"),
        ModKind.Crafted => Loc.T("крафт"),
        ModKind.Fractured => "fractured",
        ModKind.Enchant => Loc.T("энчант"),
        _ => "",
    };
    public bool HasKind => KindText.Length > 0;
    public string ToolTip => Searchable ? Label : Loc.T("Этого стата нет в поиске трейда");
    public string? Hint => Filter.Hint;
    /// <summary>The "!" badge: a flagged line that is still unticked.</summary>
    public bool ShowHint => Filter.Hint != null && !Filter.Enabled;

    public Brush Brush => Filter.IsPseudo || Filter.Kind != PriceFilterKind.Stat ? Plain : Filter.ModKind switch
    {
        ModKind.Crafted => CraftedBlue,
        ModKind.Fractured => FracturedGold,
        ModKind.Enchant => EnchantBlue,
        _ => ModBlue,
    };

    public bool Enabled
    {
        get => Filter.Enabled;
        set { if (Filter.Enabled == value) return; Filter.Enabled = value; Raise(); Raise(nameof(ShowHint)); }
    }

    string _min = Fmt(f.Min), _max = Fmt(f.Max);

    public string MinText
    {
        get => _min;
        set { if (!Set(ref _min, value)) return; Filter.Min = Parse(value); if (value.Length > 0) Enabled = Searchable; }
    }

    public string MaxText
    {
        get => _max;
        set { if (!Set(ref _max, value)) return; Filter.Max = Parse(value); if (value.Length > 0) Enabled = Searchable; }
    }

    static string Fmt(double? v) => v is double x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "";

    static double? Parse(string s) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>One offer: price, seller, how long ago it was listed.</summary>
public sealed class ListingVm(TradeListing l, double? divineChaos, PriceCheck? check = null, TopMods? top = null) : Observable
{
    static readonly Brush Ahead = Frozen(0x4A, 0xDE, 0x80);
    static readonly Brush Behind = Frozen(0xF8, 0x71, 0x71);
    static readonly Brush Same = Frozen(0xA1, 0xA1, 0xAA);
    static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    /// <summary>"Сравнить": our item against this offer, line by line, unfolded under the row.</summary>
    // only whether there is something to read: the item itself is read on the press, not when the row appears
    public bool CanCompare => check != null && l.ItemJson != null;

    bool _comparing;
    public bool IsComparing
    {
        get => _comparing;
        set
        {
            if (!Set(ref _comparing, value)) return;
            if (value && _rows == null) { _rows = BuildRows(); Raise(nameof(CompareRows)); Raise(nameof(CompareSummary)); Raise(nameof(TopSummary)); }
        }
    }

    /// <summary>A comparison line: the star for a mod that sells on its own, tiers as "T1" where known.</summary>
    public sealed record Row(string Label, string Ours, string Theirs, Brush Brush, bool Top, string? OursTier, string? TheirsTier)
    {
        public bool HasOursTier => OursTier != null;
        public bool HasTheirsTier => TheirsTier != null;
    }

    List<Row>? _rows;
    public IReadOnlyList<Row> CompareRows => _rows ?? [];

    public string OurTitle => check?.Item.Title ?? "";

    int _ahead, _behind, _ourTop, _theirTop;
    /// <summary>"у нас лучше: 3 · у них лучше: 2".</summary>
    public string CompareSummary => _rows == null ? ""
        : l.Item == null ? Loc.T("не удалось разобрать предмет с трейда")
        : Loc.T($"у нас лучше: {_ahead}  ·  у них лучше: {_behind}");

    /// <summary>"★ топ-моды: у нас 2, у них 3".</summary>
    public string TopSummary => _rows == null || l.Item == null ? "" : Loc.T($"★ топ-моды: у нас {_ourTop}, у них {_theirTop}");

    List<Row> BuildRows()
    {
        var rows = PriceCompare.Compare(check!, l, top);
        _ahead = rows.Count(r => r.Better > 0);
        _behind = rows.Count(r => r.Better < 0);
        _ourTop = rows.Count(r => r.OursTop);
        _theirTop = rows.Count(r => r.TheirsTop);
        // the lines that sell first, then the rest in the order of the filters
        return rows.OrderByDescending(r => r.Top)
            .Select(r => new Row(r.Label, r.Ours, r.Theirs, r.Better > 0 ? Ahead : r.Better < 0 ? Behind : Same, r.Top,
                TierText(r.OursTier, r.OursTierCount), TierText(r.TheirsTier, r.TheirsTierCount)))
            .ToList();
    }

    /// <summary>"T3/9" when the tier table knows the mod, "T3" when not.</summary>
    static string? TierText(int? tier, int? count) => tier is int t ? count is int n ? $"T{t}/{n}" : $"T{t}" : null;

    ImageSource? _icon;
    /// <summary>The listed item's own art, filled in once downloaded.</summary>
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    public string Price { get; } = l.Amount.ToString("0.##", CultureInfo.InvariantCulture);
    public string Currency { get; } = CurrencyName(l.Currency);
    public string Account { get; } = l.Account;
    public string Age { get; } = AgeText(l.Indexed);
    public string Extra { get; } = string.Join("  ·  ", new[]
    {
        l.ItemLevel > 0 ? $"ilvl {l.ItemLevel}" : null,
        l.Sockets,
        l.Corrupted ? Loc.T("коррапт") : null,
        l.Currency == "divine" && divineChaos is double d ? $"≈ {Math.Round(l.Amount * d)}c" : null,
    }.Where(x => !string.IsNullOrEmpty(x)));

    public static string CurrencyName(string id) => id switch
    {
        "chaos" => "chaos",
        "divine" => "divine",
        "exalted" => "exalted",
        "alch" => "alch",
        "fusing" => "fusing",
        "chance" => "chance",
        "vaal" => "vaal",
        "gcp" => "gcp",
        "jew" => "jeweller",
        "chrome" => "chromatic",
        "alt" => "alteration",
        "regal" => "regal",
        "mirror" => "mirror",
        _ => id,
    };

    static string AgeText(DateTime? at)
    {
        if (at is not DateTime t) return "";
        var d = DateTime.UtcNow - t;
        if (d.TotalMinutes < 1) return Loc.T("только что");
        if (d.TotalHours < 1) return Loc.T($"{(int)d.TotalMinutes} мин назад");
        if (d.TotalDays < 1) return Loc.T($"{(int)d.TotalHours} ч назад");
        return Loc.T($"{(int)d.TotalDays} д назад");
    }
}

/// <summary>
/// The price check window: an item copied in game, its filters, the cheapest offers. Every request to the trade site is
/// a press of the player and starts a short cooldown (a shrinking bar under the buttons), longer when the site asks for it.
/// </summary>
public sealed class PriceCheckViewModel : Observable
{
    /// <summary>The shortest pause between two requests, whatever the site would allow.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(2);
    const int MaxShown = 30;

    readonly TradeApi _api;
    readonly TradeLink _trade;
    readonly Func<PriceSettings> _settings;
    readonly Func<bool> _collapse;
    readonly double? _divineChaos;
    readonly PriceCheck _check;

    TradeSearchResult? _result;
    /// <summary>The settings of a search widened to "in person" offers; null for a plain one.</summary>
    PriceSettings? _widened;
    int _fetched;
    readonly HashSet<string> _accounts = new(StringComparer.OrdinalIgnoreCase);

    readonly NinjaPrice? _ninja;

    readonly Func<string, Task<ImageSource?>>? _art;

    public PriceCheckViewModel(PriceCheck check, TradeApi api, TradeLink trade, Func<PriceSettings> settings, Func<bool> collapse, double? divineChaos,
        NinjaPrice? ninja = null, Func<string, Task<ImageSource?>>? art = null)
    {
        _art = art;
        _ninja = ninja;
        _check = check;
        _api = api;
        _trade = trade;
        _settings = settings;
        _collapse = collapse;
        _divineChaos = divineChaos;
        Filters = new ObservableCollection<PriceFilterVm>(check.Filters.Select(f => new PriceFilterVm(f)));
        SearchCommand = new RelayCommand(() => _ = SearchAsync(), _ => CanRequest);
        MoreCommand = new RelayCommand(() => _ = MoreAsync(), _ => CanRequest && HasMore);
        OpenTradeCommand = new RelayCommand(OpenTrade);
        ToggleFiltersCommand = new RelayCommand(() => ShowAllFilters = !ShowAllFilters);
        WikiCommand = new RelayCommand(() => Open($"https://www.poewiki.net/wiki/{Uri.EscapeDataString(WikiName.Replace(' ', '_'))}"));
        _timeless = TimelessJewel.From(Item.Name, Item.Mods.Select(m => m.Text));
        TimelessCommand = new RelayCommand(() => { if (_timeless != null) Open(_timeless.Url()); });
        PoedbCommand = new RelayCommand(() => Open($"https://poedb.tw/us/{Uri.EscapeDataString(WikiName.Replace(' ', '_').Replace("'", ""))}"));
        ApplyFold();
    }

    /// <summary>A unique or a gem by its own name, anything else by its base.</summary>
    string WikiName => Item.Rarity == ItemRarity.Unique && Item.Name.Length > 0 ? Item.Name : Item.BaseType.Length > 0 ? Item.BaseType : Item.Title;

    public RelayCommand WikiCommand { get; }
    public RelayCommand PoedbCommand { get; }

    readonly TimelessJewel? _timeless;
    /// <summary>A timeless jewel: Vilsol's calculator on its seed (the socket is not in a copy from the game).</summary>
    public RelayCommand TimelessCommand { get; }
    public bool HasTimeless => _timeless != null;

    static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    #region Folding

    bool _showAll;
    public bool ShowAllFilters { get => _showAll; set { if (Set(ref _showAll, value)) ApplyFold(); } }
    public RelayCommand ToggleFiltersCommand { get; }
    int FoldedCount => Filters.Count(f => !f.Filter.Enabled && !_ticked.Contains(f));
    readonly HashSet<PriceFilterVm> _ticked = new();
    // nothing ticked at all (a unique): everything is shown, so there is nothing to unfold
    public bool HasFolded => _ticked.Count > 0 && FoldedCount > 0;
    public string FoldText => ShowAllFilters ? Loc.T("▴ Свернуть") : Loc.T($"▸ Ещё фильтров: {FoldedCount}");

    void ApplyFold()
    {
        if (_ticked.Count == 0) foreach (var f in Filters.Where(f => f.Filter.Shown)) _ticked.Add(f);
        // a short window: what is ticked stays, the rest waits under the fold; nothing is hidden while unfolded
        foreach (var f in Filters) f.IsHidden = !ShowAllFilters && !_ticked.Contains(f);
        // nothing ticked at all (a unique): show everything, a fold over an empty list helps nobody
        if (_ticked.Count == 0) foreach (var f in Filters) f.IsHidden = false;
        Raise(nameof(HasFolded));
        Raise(nameof(FoldText));
    }

    #endregion

    /// <summary>The player's top-mod lists from the settings (the comparison's star).</summary>
    public TopMods TopMods { get; init; } = TopMods.Default;

    public GameItem Item => _check.Item;
    public string Title => Item.Title;
    public string? BaseLine => Item.Name.Length > 0 && Item.BaseType != Item.Name ? Item.BaseType : null;
    public bool HasBaseLine => BaseLine != null;
    public string League => _trade.League;
    public string? RateText => _divineChaos is double d ? $"1 div ≈ {Math.Round(d)}c" : null;
    public bool HasRate => _divineChaos != null;

    /// <summary>Hover over the rate: 0.1 … 1 divine in chaos, for paying in parts.</summary>
    public string? RateTable => _divineChaos is double d
        ? string.Join(Environment.NewLine, Enumerable.Range(1, 10).Select(i =>
            (i / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + $" div  =  {Math.Round(d * i / 10)}c"))
        : null;

    /// <summary>poe.ninja for a unique: "poe.ninja 44c" with no trade request spent.</summary>
    public string? NinjaText => _ninja is { } n
        ? "poe.ninja " + (n.Divine >= 1
            ? n.Divine.ToString("0.#", CultureInfo.InvariantCulture) + " div"
            : Math.Round(n.Chaos, n.Chaos < 10 ? 1 : 0).ToString(CultureInfo.InvariantCulture) + "c")
        : null;
    public bool HasNinja => _ninja != null;

    string? _lowest;
    /// <summary>The cheapest offer found: the answer at a glance.</summary>
    public string? LowestText { get => _lowest; private set { Set(ref _lowest, value); Raise(nameof(HasLowest)); } }
    public bool HasLowest => !string.IsNullOrEmpty(_lowest);

    public Brush RarityBrush => Item.Rarity switch
    {
        ItemRarity.Unique => Rarity(0xAF, 0x60, 0x25),
        ItemRarity.Rare => Rarity(0xFF, 0xFF, 0x77),
        ItemRarity.Magic => Rarity(0x88, 0x88, 0xFF),
        _ when Item.IsGem => Rarity(0x1B, 0xA2, 0x9B),
        _ => Rarity(0xC8, 0xC8, 0xC8),
    };

    static Brush Rarity(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    public ObservableCollection<PriceFilterVm> Filters { get; }
    public bool HasFilters => Filters.Count > 0;
    public ObservableCollection<ListingVm> Listings { get; } = new();

    #region Chips

    public bool CanToggleBase => !_check.BaseOnly && Item.BaseType.Length > 0;
    public string BaseChip => Item.BaseType;
    public bool UseBase { get => _check.UseBase; set { _check.UseBase = value; Raise(); } }

    public bool HasItemLevel => Item.ItemLevel > 0 && !Item.IsGem;
    public string ItemLevelChip => $"ilvl ≥ {Item.ItemLevel}";
    public bool UseItemLevel { get => _check.UseItemLevel; set { _check.UseItemLevel = value; Raise(); } }

    // three-state chips: true = only such, false = none such ("Нет" badge), null = any
    public bool? CorruptedState { get => _check.Corrupted; set { _check.Corrupted = value; Raise(); } }
    public bool? MirroredState { get => _check.Mirrored; set { _check.Mirrored = value; Raise(); } }
    public bool? FracturedState { get => _check.Fractured; set { _check.Fractured = value; Raise(); } }
    public bool? UnidState { get => _check.Unidentified; set { _check.Unidentified = value; Raise(); } }
    public bool? FoulbornState { get => _check.Foulborn; set { _check.Foulborn = value; Raise(); } }
    public bool IsEquipment => _check.Category != null;
    public bool IsUnique => Item.Rarity == ItemRarity.Unique;
    public bool HasFractured => Item.FracturedItem || Item.Mods.Any(m => m.Kind == ModKind.Fractured);

    public bool HasQuality => Item.Quality > 0;
    public string QualityChip => Loc.T($"Качество ≥ {Item.Quality}%");
    public bool UseQuality { get => _check.UseQuality; set { _check.UseQuality = value; Raise(); } }

    bool _exact;
    /// <summary>Minimums at the item's own values instead of the strictness share.</summary>
    public bool ExactValues
    {
        get => _exact;
        set
        {
            if (!Set(ref _exact, value)) return;
            foreach (var f in Filters)
                if (f.Filter.Value is double v && v > 0)
                    f.MinText = (value ? v : f.Filter.DefaultMin ?? v).ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
    public bool HasExact => Filters.Any(f => f.Filter.Value is > 0);

    public bool HasLevelCap => _check.LevelCap > 0 && _check.Category != null && !Item.IsGem;
    public string LevelCapChip => Loc.T($"Требует ур. ≤ {_check.LevelCap}");
    public bool UseLevelCap { get => _check.UseLevelCap; set { _check.UseLevelCap = value; Raise(); } }

    public bool HasLinks => Item.Links >= 5;
    public string LinksChip => $"{Item.Links}L";
    public bool UseLinks { get => _check.UseLinks; set { _check.UseLinks = value; Raise(); } }

    public bool HasRarityChip => Item.Rarity == ItemRarity.Magic && _check.Category != null;
    public bool UseRarity { get => _check.UseRarity; set { _check.UseRarity = value; Raise(); } }

    // a cluster from the build: its wanted notables, the rest any of those that keep off their path
    public bool HasClusterLayout => _check.Cluster != null;
    public bool UseClusterLayout { get => _check.UseClusterLayout; set { _check.UseClusterLayout = value; Raise(); } }
    public string ClusterLayoutChip => _check.Cluster is { } p
        ? Loc.T($"+{p.Count} любой не на пути · {p.Others.Count} вар.")
        : "";
    public string ClusterLayoutTip => _check.Cluster is { } p
        ? Loc.T($"Мои нотабли ({string.Join(", ", p.Wanted)}) обязательны, остальные места — любой из: {string.Join(", ", p.Others)}. Они встают на кольце дальше моих, и к моим не надо идти через них. Не подходят ({p.Skipped}): встали бы на пути.")
        : "";

    public bool HasAreaLevel => Item.HeistJob != null && Item.AreaLevel > 0;
    public string AreaLevelChip => Loc.T($"Уровень зоны ≥ {Item.AreaLevel}");
    public bool UseAreaLevel { get => _check.UseAreaLevel; set { _check.UseAreaLevel = value; Raise(); } }

    public bool HasHeistJob => Item.HeistJob != null;
    public string HeistJobChip => $"{Item.HeistJob} ≥ {Item.HeistJobLevel}";
    public bool UseHeistJob { get => _check.UseHeistJob; set { _check.UseHeistJob = value; Raise(); } }

    public string? InfoChip => Item.IsGem ? Loc.T($"ур. {Item.GemLevel} · {Item.Quality}%") : Item.MapTier is int t ? $"T{t}" : null;
    public bool HasInfoChip => InfoChip != null;

    #endregion

    #region Requests and cooldown

    bool _busy;
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RaiseCan(); } }

    DateTime _readyAt = DateTime.MinValue;
    DateTime _cooldownFrom;
    DispatcherTimer? _tick;

    public bool CanRequest => !_busy && DateTime.UtcNow >= _readyAt;
    public bool IsCoolingDown => DateTime.UtcNow < _readyAt;

    /// <summary>1 → 0 while the cooldown runs: the bar under the buttons.</summary>
    public double CooldownLeft
    {
        get
        {
            var total = (_readyAt - _cooldownFrom).TotalMilliseconds;
            if (total <= 0) return 0;
            return Math.Clamp((_readyAt - DateTime.UtcNow).TotalMilliseconds / total, 0, 1);
        }
    }

    void StartCooldown(TimeSpan span)
    {
        var until = DateTime.UtcNow + span;
        if (until <= _readyAt) return;
        _cooldownFrom = DateTime.UtcNow;
        _readyAt = until;
        _tick ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _tick.Tick -= OnTick;
        _tick.Tick += OnTick;
        _tick.Start();
        OnTick(null, EventArgs.Empty);
    }

    void OnTick(object? sender, EventArgs e)
    {
        Raise(nameof(CooldownLeft));
        Raise(nameof(IsCoolingDown));
        if (DateTime.UtcNow >= _readyAt)
        {
            _tick?.Stop();
            RaiseCan();
        }
    }

    void RaiseCan()
    {
        Raise(nameof(CanRequest));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>A cooldown left from the previous window carries over: the hotkey cannot be used to spam either.</summary>
    public void InheritCooldown(DateTime readyAt)
    {
        if (readyAt > DateTime.UtcNow) StartCooldown(readyAt - DateTime.UtcNow);
    }

    public DateTime ReadyAt => _readyAt;

    string? _status;
    public string? Status { get => _status; private set { Set(ref _status, value); Raise(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

    bool _isError;
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }

    public RelayCommand SearchCommand { get; }
    public RelayCommand MoreCommand { get; }
    public RelayCommand OpenTradeCommand { get; }

    public bool HasMore => _result != null && _fetched < Math.Min(_result.ItemIds.Count, MaxShown);
    public bool HasListings => Listings.Count > 0;

    public async Task SearchAsync()
    {
        if (!CanRequest) return;
        IsBusy = true;
        IsError = false;
        Status = Loc.T("Ищу…");
        try
        {
            _result = null;
            _fetched = 0;
            _accounts.Clear();
            Listings.Clear();
            Compared = null;
            LowestText = null;
            var settings = _settings();
            _result = await _api.SearchAsync(_trade.League, _check.Query(_trade, settings));
            var widened = false;
            // nothing with instant buyout (league items, threads): the same search among "in person" offers too,
            // when the site lets one more request through right away
            // only with room to spare: an automatic extra search must never be the one that gets the player banned
            if (_result.ItemIds.Count == 0 && settings.Status == "securable" && _api.NextSearchWait <= TimeSpan.FromSeconds(4) && _api.SearchesLeft >= 3)
            {
                Status = Loc.T("С мгновенным выкупом нет — ищу и среди «лично»…");
                await Task.Delay(_api.NextSearchWait);
                _widened = settings with { Status = "available" };
                _result = await _api.SearchAsync(_trade.League, _check.Query(_trade, _widened));
                widened = true;
            }
            else _widened = null;
            if (_result.ItemIds.Count == 0)
            {
                Status = Loc.T("Ничего не нашлось — сними пару галочек или понизь минимумы");
            }
            else
            {
                await FetchNextAsync();
                Status = Loc.T($"Найдено: {_result.Total}") + (widened ? Loc.T(" (с мгновенным выкупом — 0, показаны «лично»)") : "") + HintNote;
            }
        }
        catch (TradeRateLimitException ex)
        {
            Fail(ex.Message);
            StartCooldown(ex.Wait);
        }
        catch (Exception ex) when (ex is TradeApiException or HttpRequestException or TaskCanceledException)
        {
            Fail(ex is TradeApiException ? ex.Message : Loc.T("Нет связи с сайтом трейда"));
        }
        finally
        {
            IsBusy = false;
            StartCooldown(Cooldown > _api.NextSearchWait ? Cooldown : _api.NextSearchWait);
            Raise(nameof(HasMore));
            Raise(nameof(HasListings));
        }
    }

    async Task MoreAsync()
    {
        if (!CanRequest || !HasMore) return;
        IsBusy = true;
        try { await FetchNextAsync(); }
        catch (TradeRateLimitException ex) { Fail(ex.Message); StartCooldown(ex.Wait); }
        catch (Exception ex) when (ex is TradeApiException or HttpRequestException or TaskCanceledException)
        {
            Fail(ex is TradeApiException ? ex.Message : Loc.T("Нет связи с сайтом трейда"));
        }
        finally
        {
            IsBusy = false;
            StartCooldown(Cooldown > _api.NextSearchWait ? Cooldown : _api.NextSearchWait);
            Raise(nameof(HasMore));
            Raise(nameof(HasListings));
        }
    }

    async Task FetchNextAsync()
    {
        if (_result == null) return;
        var ids = _result.ItemIds.Skip(_fetched).Take(10).ToList();
        var listings = await _api.FetchAsync(_result.Id, ids);
        _fetched += ids.Count;
        var collapse = _collapse();
        foreach (var l in listings)
        {
            // one seller, one line: the rest of their stash would push other offers out
            if (collapse && l.Account.Length > 0 && !_accounts.Add(l.Account)) continue;
            var row = new ListingVm(l, _divineChaos, _check, TopMods);
            row.PropertyChanged += OnRowChanged;
            Listings.Add(row);
            if (_art != null && l.Icon is { } url) _ = LoadArtAsync(row, url);
        }
        if (Listings.Count > 0) LowestText = Loc.T($"от {Listings[0].Price} {Listings[0].Currency}");
        if (_result.Total > 0) Status = Loc.T($"Найдено: {_result.Total}") + (_widened != null ? Loc.T(" (с мгновенным выкупом — 0, показаны «лично»)") : "") + HintNote;
    }

    ListingVm? _compared;
    /// <summary>The offer shown in the comparison window beside this one; one at a time.</summary>
    public ListingVm? Compared { get => _compared; private set => Set(ref _compared, value); }

    void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ListingVm.IsComparing) || sender is not ListingVm row) return;
        if (row.IsComparing)
        {
            foreach (var other in Listings) if (other != row) other.IsComparing = false;
            Compared = row;
        }
        else if (Compared == row) Compared = null;
    }

    /// <summary>The comparison window closed on its own (its cross, Esc): the row's toggle goes off too.</summary>
    public void CloseCompare()
    {
        if (Compared is { } row) row.IsComparing = false;
        Compared = null;
    }

    async Task LoadArtAsync(ListingVm row, string url)
    {
        try { row.Icon = await _art!(url); }
        catch (Exception) { /* no picture: the row stays without one */ }
    }

    /// <summary>A reminder under the results while a "!" line is still left out of the search.</summary>
    string HintNote => Filters.Any(f => f.ShowHint) ? Loc.T("  ·  ⚠ есть стат с «!» — проверь цену с ним") : "";

    void Fail(string message)
    {
        IsError = true;
        Status = message;
    }

    void OpenTrade()
    {
        string url = _result is { Id.Length: > 0 } r
            ? $"https://www.pathofexile.com/trade/search/{Uri.EscapeDataString(_trade.League)}/{r.Id}"
            : _trade.SearchUrl(_check.Query(_trade, _widened ?? _settings()));
        Open(url);
    }

    #endregion
}
