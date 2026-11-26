using FreshExile.Core;

namespace FreshExile.App;

/// <summary>Price check settings and the item → window model step.</summary>
public sealed partial class MainViewModel
{
    TradeApi? _tradeApi;
    double? _divineChaos;
    string? _divineLeague;
    DateTime _divineAt;
    DateTime _priceReadyAt = DateTime.MinValue;

    TradeApi TradeApi => _tradeApi ??= new TradeApi(_http) { Log = Log.Write };
    NinjaUniques? _ninjaUniques;
    NinjaExchange? _ninjaExchange;

    /// <summary>Puts a seller's message into the game's chat (set by the main window: it owns the hotkeys and the game's handle).</summary>
    public Func<string, Task<bool>>? SendChat { get; set; }

    PriceSettings CurrentPriceSettings() => new()
    {
        Status = TradeStatus,
        Strictness = (int)TradeStrictness,
        Currency = State.PriceCurrency ?? "",
        Listed = State.PriceListed ?? "",
    };

    /// <summary>The window model for copied item text, or the line to show instead.</summary>
    /// <param name="pick">Lines to tick from the start (a PoB unique's version line, opened from the gear tab).</param>
    public async Task<(PriceCheckViewModel? Vm, string? Error)> PreparePriceCheckAsync(string? text, IReadOnlyCollection<string>? pick = null)
    {
        if (GameItem.IsOtherLanguage(text))
            return (null, Loc.T("Прайс-чек пока понимает только английский клиент игры. Переключи язык игры на English."));
        var item = GameItem.Parse(text);
        if (item == null)
            return (null, Loc.T($"Наведи мышь на предмет в игре и нажми {KeyText(HotkeyActions.Price)}. Можно и так: Ctrl+C на предмете, потом сочетание вне игры."));
        if (item.Unidentified && item.Rarity != ItemRarity.Unique)
            return (null, Loc.T("Предмет не опознан: цена зависит от модов, сначала опознай его."));
        return await PreparePriceCheckAsync(item, pick);
    }

    const int MaxRecentSearches = 10;

    /// <summary>A name picked in the search window goes on top of the recent ones.</summary>
    public void RememberSearch(string text)
    {
        var recent = State.RecentSearches;
        recent.RemoveAll(s => s.Equals(text, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, text);
        if (recent.Count > MaxRecentSearches) recent.RemoveRange(MaxRecentSearches, recent.Count - MaxRecentSearches);
        Save();
    }

    /// <summary>The trade's name list for the search window; null while the site is out of reach.</summary>
    public async Task<TradeLink?> TradeForSearchAsync()
    {
        try { return await EnsureTradeAsync(); }
        catch (Exception) { _tradeLoading = null; return null; }
    }

    /// <summary>The window model for an item already in hand: copied in game, or picked by name in the search window.</summary>
    public async Task<(PriceCheckViewModel? Vm, string? Error)> PreparePriceCheckAsync(GameItem item, IReadOnlyCollection<string>? pick = null)
    {
        TradeLink trade;
        try { trade = await EnsureTradeAsync(); }
        catch (Exception)
        {
            _tradeLoading = null;
            return (null, Loc.T("Нет связи с сайтом трейда."));
        }
        trade.Status = TradeStatus;

        // the rate moves during a session: asked again after an hour
        if (_divineLeague != trade.League || DateTime.UtcNow - _divineAt > TimeSpan.FromHours(1))
        {
            _divineLeague = trade.League;
            _divineAt = DateTime.UtcNow;
            // poe.ninja is a nicety: never worth holding the window for
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            _divineChaos = await NinjaRates.DivineChaosAsync(_http, trade.League, cts.Token);
            if (_divineChaos == null) _divineLeague = null;
        }

        var check = PriceCheck.For(item, trade, (int)TradeStrictness, _gameLevel, WantedNotables(item, pick));
        // a line the trade does not know: logged as read, odd characters spelled out (an invisible one breaks the match)
        foreach (var f in check.Filters.Where(f => !f.Searchable))
            Log.Write($"price check: no trade stat for \"{string.Concat(f.Label.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"))}\"");
        if (pick is { Count: > 0 })
            foreach (var f in check.Filters)
                if (f.Searchable && pick.Contains(f.Label, StringComparer.OrdinalIgnoreCase)) { f.Enabled = true; f.Shown = true; }
        NinjaPrice? ninja;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            ninja = trade.ExchangeId(item) is { } exchangeId
                // currency and the like: the in-game exchange's rate is the price, the trade's few bulk sellers are not
                ? await (_ninjaExchange ??= new NinjaExchange(_http)).ChaosAsync(exchangeId, trade.League, trade.ExchangeGroup(exchangeId), cts.Token) is double chaos
                    ? new NinjaPrice(chaos, _divineChaos is double r and > 0 ? chaos / r : 0, 0) { Market = true }
                    : null
                : await (_ninjaUniques ??= new NinjaUniques(_http)).PriceAsync(item, trade.League, cts.Token);
        // the sellers setting holds for gems too (they were silently widened to "in person" and a picked Instant showed whispers
        // from days ago); with no instant offer at all the search still widens by itself and says so
        var vm = new PriceCheckViewModel(check, TradeApi, trade, CurrentPriceSettings, () => State.PriceCollapse, _divineChaos, ninja, LoadArtAsync)
        {
            TopMods = CurrentTopMods(),
            SendWhisper = SendChat,
        };
        AddOils(vm);
        vm.InheritCooldown(_priceReadyAt);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PriceCheckViewModel.CooldownLeft)) _priceReadyAt = vm.ReadyAt; };
        return (vm, null);
    }

    /// <summary>An anointment line gets its three oils (the tree's recipe), their icons loaded from the icon cache.</summary>
    void AddOils(PriceCheckViewModel vm)
    {
        if (Tree is not { } tree) return;
        const string Prefix = "Allocates ";
        foreach (var f in vm.Filters)
        {
            if (!f.Label.StartsWith(Prefix, StringComparison.Ordinal) || tree.AnointRecipe(f.Label[Prefix.Length..].Trim()) is not { } recipe) continue;
            var oils = recipe.Select(OilVm.Of).ToList();
            f.Oils = oils;
            foreach (var oil in oils) _ = LoadOilAsync(oil);
        }
    }

    async Task LoadOilAsync(OilVm oil)
    {
        if (await ItemIcons.FileFor(_http, oil.IconUrl) is { } path && LoadPicture(path) is { } pic) oil.Icon = pic;
    }

    /// <summary>
    /// A cluster jewel: the notables the build takes. From the gear tab they come with the request; a jewel copied in
    /// game is matched with the build's cluster of the same size and enchant. Null = no build to go by (all notables count).
    /// </summary>
    IReadOnlyCollection<string>? WantedNotables(GameItem item, IReadOnlyCollection<string>? pick)
    {
        if (!item.BaseType.EndsWith("Cluster Jewel", StringComparison.OrdinalIgnoreCase)) return null;
        const string Prefix = "1 Added Passive Skill is ";
        var picked = pick?.Where(p => p.StartsWith(Prefix, StringComparison.Ordinal)).Select(p => p[Prefix.Length..].Trim()).ToList();
        if (picked is { Count: > 0 }) return picked;

        var grant = item.Mods.FirstOrDefault(m => m.Text.StartsWith("Added Small Passive Skills grant:", StringComparison.Ordinal))?.Text;
        var ours = GearJewels.FirstOrDefault(g => g.Item.BaseType == item.BaseType && g.NotableTaken.Count > 0
                                                 && g.Item.Mods.Any(m => m.Text == grant));
        var taken = ours?.NotableTaken.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        return taken is { Count: > 0 } ? taken : null;
    }

    public string PriceHint =>
        Loc.T($"{KeyText(HotkeyActions.Price)} в игре над предметом: окно по центру с модами и самыми дешёвыми предложениями. Продавцы, подбор статов и лига — из блока «Поиск» выше. Между запросами короткая пауза (полоска под кнопкой), чтобы трейд не выдал бан.");

    /// <summary>An offer's picture: downloaded once into the icon cache, loaded without keeping the file locked.</summary>
    Task<System.Windows.Media.ImageSource?> LoadArtAsync(string url)
    {
        // ten offers often share one picture: one download each, or two writes race on the same temp file and one row stays blank
        if (_art.TryGetValue(url, out var running) && !running.IsFaulted && !(running.IsCompleted && running.Result == null)) return running;
        return _art[url] = LoadArtOnceAsync(url);
    }

    readonly Dictionary<string, Task<System.Windows.Media.ImageSource?>> _art = new();

    async Task<System.Windows.Media.ImageSource?> LoadArtOnceAsync(string url)
    {
        var file = await ItemIcons.FileFor(_http, url);
        if (file == null) return null;
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(file);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    #region Top mods (the comparison's star)

    TopMods? _topMods;
    TopMods CurrentTopMods() => _topMods ??= new TopMods(State.TopMods, State.TopModShare);

    /// <summary>A slot chip of the settings: ticking one shows its list.</summary>
    public sealed class TopSlot(string key, string name, Action<TopSlot> select) : Observable
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        bool _on;
        public bool IsSelected
        {
            get => _on;
            set { if (Set(ref _on, value) && value) select(this); }
        }
    }

    IReadOnlyList<TopSlot>? _topSlots;
    public IReadOnlyList<TopSlot> TopSlots => _topSlots ??= TopMods.Slots.Select(s => new TopSlot(s.Key, s.Name, SelectTopSlot)).ToList();

    TopSlot? _topSlot;
    TopSlot TopSlotSelected
    {
        get
        {
            if (_topSlot == null) { _topSlot = TopSlots[0]; _topSlot.IsSelected = true; }
            return _topSlot;
        }
    }

    void SelectTopSlot(TopSlot slot)
    {
        if (slot == _topSlot) return;
        _topSlot = slot;
        foreach (var s in TopSlots) if (s != slot) s.IsSelected = false;
        Raise(nameof(TopModsText));
        Raise(nameof(TopPhrases));
        Raise(nameof(TopModsEdited));
    }

    /// <summary>The selected slot's phrases, one per line.</summary>
    public string TopModsText
    {
        get
        {
            var key = TopSlotSelected.Key;
            var list = State.TopMods.TryGetValue(key, out var mine) ? mine : TopMods.Defaults[key];
            return string.Join(Environment.NewLine, list);
        }
        set
        {
            var key = TopSlotSelected.Key;
            var list = (value ?? "").Replace("\r", "").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (list.SequenceEqual(TopMods.Defaults[key])) State.TopMods.Remove(key);
            else State.TopMods[key] = list;
            _topMods = null;
            Save();
            Raise(nameof(TopModsEdited));
        }
    }

    /// <summary>The selected slot's phrases as chips (the same list as <see cref="TopModsText"/>).</summary>
    public IReadOnlyList<string> TopPhrases => TopModsText.Replace("\r", "").Split('\n').Where(s => s.Trim().Length > 0).ToList();

    RelayCommand? _removeTopPhrase;
    public RelayCommand RemoveTopPhraseCommand => _removeTopPhrase ??= new RelayCommand(p =>
    {
        if (p is not string phrase) return;
        var list = TopPhrases.ToList();
        list.Remove(phrase);
        TopModsText = string.Join("\n", list);
        Raise(nameof(TopPhrases));
    });

    /// <summary>Typed or pasted phrases: several lines become several chips; one already in the list is not doubled.</summary>
    public void AddTopPhrases(string text)
    {
        var list = TopPhrases.ToList();
        foreach (var p in text.Replace("\r", "").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0))
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        TopModsText = string.Join("\n", list);
        Raise(nameof(TopPhrases));
    }

    /// <summary>The selected slot differs from the default: "Сбросить" is on.</summary>
    public bool TopModsEdited => State.TopMods.ContainsKey(TopSlotSelected.Key);

    RelayCommand? _resetTopMods;
    public RelayCommand ResetTopModsCommand => _resetTopMods ??= new RelayCommand(() =>
    {
        State.TopMods.Remove(TopSlotSelected.Key);
        _topMods = null;
        Save();
        Raise(nameof(TopModsText));
        Raise(nameof(TopPhrases));
        Raise(nameof(TopModsEdited));
    });

    void SetTopShare(int share, bool on)
    {
        if (!on || State.TopModShare == share) return;
        State.TopModShare = share;
        _topMods = null;
        Save();
        foreach (var n in new[] { nameof(IsTopThird), nameof(IsTopHalf), nameof(IsTopAnyTier) }) Raise(n);
    }

    public bool IsTopThird { get => State.TopModShare == 3; set => SetTopShare(3, value); }
    public bool IsTopHalf { get => State.TopModShare == 2; set => SetTopShare(2, value); }
    public bool IsTopAnyTier { get => State.TopModShare <= 0; set => SetTopShare(0, value); }

    #endregion

    public bool PriceAutoSearch
    {
        get => State.PriceSearchOnOpen;
        set { if (State.PriceSearchOnOpen == value) return; State.PriceSearchOnOpen = value; Raise(); Save(); }
    }

    public bool PriceCollapse
    {
        get => State.PriceCollapse;
        set { if (State.PriceCollapse == value) return; State.PriceCollapse = value; Raise(); Save(); }
    }

    void SetPriceCurrency(string v, bool on)
    {
        if (!on || (State.PriceCurrency ?? "") == v) return;
        State.PriceCurrency = v;
        Save();
        foreach (var n in new[] { nameof(IsPriceAnyCurrency), nameof(IsPriceChaos), nameof(IsPriceDivine), nameof(IsPriceChaosDivine) }) Raise(n);
    }

    public bool IsPriceAnyCurrency { get => (State.PriceCurrency ?? "") == ""; set => SetPriceCurrency("", value); }
    public bool IsPriceChaos { get => State.PriceCurrency == "chaos"; set => SetPriceCurrency("chaos", value); }
    public bool IsPriceDivine { get => State.PriceCurrency == "divine"; set => SetPriceCurrency("divine", value); }
    public bool IsPriceChaosDivine { get => State.PriceCurrency == "chaos_divine"; set => SetPriceCurrency("chaos_divine", value); }

    void SetPriceListed(string v, bool on)
    {
        if (!on || (State.PriceListed ?? "") == v) return;
        State.PriceListed = v;
        Save();
        foreach (var n in new[] { nameof(IsListedAny), nameof(IsListedDay), nameof(IsListed3Days), nameof(IsListedWeek) }) Raise(n);
    }

    public bool IsListedAny { get => (State.PriceListed ?? "") == ""; set => SetPriceListed("", value); }
    public bool IsListedDay { get => State.PriceListed == "1day"; set => SetPriceListed("1day", value); }
    public bool IsListed3Days { get => State.PriceListed == "3days"; set => SetPriceListed("3days", value); }
    public bool IsListedWeek { get => State.PriceListed == "1week"; set => SetPriceListed("1week", value); }
}
