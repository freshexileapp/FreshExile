using System.Collections.ObjectModel;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>
/// Compact mode as a tracker: one line out in the field, gem shopping only while standing in a town.
/// </summary>
public sealed partial class MainViewModel
{
    bool _inTown;
    int? _currentAct;

    public ObservableCollection<GemRowVm> TownGems { get; } = new();

    /// <summary>The town list grouped by who sells the gem: one search string per vendor, the current act's vendors first.</summary>
    public ObservableCollection<VendorGroupVm> TownVendors { get; } = new();

    public bool ShowMini => IsCompact && Build != null;
    public bool ShowFullStage => ShowStage && !IsCompact;
    public bool ShowTownBlock => ShowMini && Tracking && _inTown && (TownGems.Count > 0 || ShowTownSwap);
    /// <summary>The swap part of the town block: off with "связки" unticked in the settings.</summary>
    public bool ShowTownSwap => ShowMiniSwap && TrackGemLinks;
    public bool HasTownGems => TownGems.Count > 0;
    /// <summary>"ур. 38", empty until the log tells the level.</summary>
    public string MiniLevel => _gameLevel > 0 ? Loc.T($"ур. {_gameLevel}") : "";
    public string TownCaption => TownGems.Count > 0 ? Loc.T($"КУПИТЬ В ГОРОДЕ · {TownGems.Count}") : Loc.T("В ГОРОДЕ");

    readonly RunTimer _runTimer = new();
    System.Windows.Threading.DispatcherTimer? _runClock;

    /// <summary>"акт 12:34" / "карта 4:12" in the middle of the mini line; see <see cref="RunTimer"/>.</summary>
    public string RunTimerText => _runTimer.Text(DateTime.Now);
    public string RunTimerLabel => _runTimer.Label;
    public string RunTimerTime => _runTimer.Time(DateTime.Now);
    // only reads the log, no screen capture: stays on with "Следить за игрой" off
    public bool HasRunTimer => _runTimer.Current != RunTimer.Kind.None;
    public string RunTimerTip => _runTimer.Current == RunTimer.Kind.Map
        ? _runTimer.Stopped ? Loc.T("Время последней карты (стоит, пока ты в хайдауте)") : Loc.T("Время на этой карте")
        : Loc.T("Время в этом акте");

    void RaiseRunTimer()
    {
        foreach (var n in new[] { nameof(RunTimerText), nameof(RunTimerLabel), nameof(RunTimerTime), nameof(HasRunTimer), nameof(RunTimerTip) })
            Raise(n);
    }

    void StartRunClock()
    {
        _runClock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runClock.Tick += (_, _) => { if (ShowMini) RaiseRunTimer(); };
        _runClock.Start();
    }

    void OnArea(AreaGenerated a)
    {
        // the time in the log line; an old one (the game was closed long ago) would make the timer lie
        var at = a.At is { } t && Math.Abs((DateTime.Now - t).TotalMinutes) < 10 ? t : DateTime.Now;
        _runTimer.OnArea(a, at);
        RaiseRunTimer();
        _currentAct = _follower.Act;
        _inTown = _follower.InTown;
        RefreshTownGems();
        RaiseChoices();
    }

    /// <summary>Gems of this stage still to buy that are already sold by now (not those unlocked in later acts).</summary>
    void RefreshTownGems()
    {
        TownGems.Clear();
        var act = _currentAct ?? 99;
        // "гемы" unticked in the settings: no shopping list (expensive gems bought on the trade site nag otherwise)
        if (TrackGems)
        foreach (var r in Groups.SelectMany(g => g.Rows).GroupBy(r => r.Gem.Key).Select(g => g.First()))
            if (!r.IsBought && (r.From == null || r.From.Act <= act))
                TownGems.Add(r);
        _vendorSearch = null;
        TownVendors.Clear();
        var groups = TownGems
            .GroupBy(r => r.FromLab ? "lab" : r.From?.Npc ?? "")
            .Select(g => (Key: g.Key, Act: g.First().FromLab ? 99 : g.First().From?.Act ?? 98, Rows: g.ToList()))
            .OrderBy(g => g.Act == act ? 0 : 1).ThenBy(g => g.Act).ThenBy(g => g.Key, StringComparer.Ordinal);
        foreach (var (key, gAct, rows) in groups)
        {
            var lab = key == "lab";
            var title = lab ? Loc.T("Лаба") : key.Length == 0 ? Loc.T("Другие") : key;
            var where = lab || gAct >= 98 ? "" : Loc.T($"Акт {gAct}");
            TownVendors.Add(new VendorGroupVm(title, where, rows, lab ? null : () =>
                VendorSearch.Build(rows.Select(r => _gems?.Find(r.Gem)?.Name ?? r.Name), _gems?.Names ?? rows.Select(r => r.Name)),
                TrySetClipboard));
        }
        foreach (var n in new[] { nameof(ShowTownBlock), nameof(HasTownGems), nameof(TownCaption) })
            Raise(n);
    }

    /// <summary>Settings, under "Следить за игрой": what to buy in town.</summary>
    public bool TrackGems
    {
        get => _s.TrackGems;
        set
        {
            if (_s.TrackGems == value) return;
            _s.TrackGems = value;
            Raise();
            RefreshTownGems();
            Save();
        }
    }

    /// <summary>Settings, under "Следить за игрой": what to swap in the sockets.</summary>
    public bool TrackGemLinks
    {
        get => _s.TrackLinks;
        set
        {
            if (_s.TrackLinks == value) return;
            _s.TrackLinks = value;
            Raise();
            Raise(nameof(ShowTownSwap));
            Raise(nameof(ShowTownBlock));
            Save();
        }
    }

    /// <summary>QA: "--demo-town" pretends the character stands in a town.</summary>
    public void DemoTown()
    {
        _inTown = true;
        _currentAct ??= 4;
        RefreshTownGems();
    }

    /// <summary>"помочь Alira — убить Kraityn и Oak" from PoB; null when the build does not say.</summary>
    public string? BanditText => BuildChoices.BanditText(Build?.Bandit);
    public bool HasBandit => BanditText != null;
    /// <summary>"The Brine King + Ryslatha".</summary>
    public string? PantheonText => Build == null ? null : BuildChoices.PantheonText(Build.PantheonMajor, Build.PantheonMinor);
    public bool HasPantheon => PantheonText != null;
    public bool HasChoices => HasBandit || HasPantheon;

    /// <summary>The bandit line in the compact panel only while in Act 2, where the choice is made.</summary>
    public bool ShowMiniBandit => ShowMini && HasBandit && ChoiceDue(BuildChoices.BanditAct, BuildChoices.BanditMaxLevel);
    /// <summary>The pantheon line in the compact panel in the act its first god is freed.</summary>
    public bool ShowMiniPantheon => ShowMini && HasPantheon && ChoiceDue(BuildChoices.PantheonAct, BuildChoices.PantheonMaxLevel);

    /// <summary>"Бандиты: помочь Alira · Пантеон: The Brine King + Ralakesh" under the open build in the switcher.</summary>
    public string ChoicesLine => string.Join(Environment.NewLine, new[]
    {
        HasBandit ? Loc.T($"Бандиты: {BanditText}") : null,
        HasPantheon ? Loc.T($"Пантеон: {PantheonText}") : null,
    }.OfType<string>());

    /// <summary>The full panel's plate, while in the act of the choice and until ✕ on this stage.</summary>
    public bool ShowBanditPlate => !IsCompact && HasBandit && ChoiceDue(BuildChoices.BanditAct, BuildChoices.BanditMaxLevel) && !PlateHidden("bandit");
    public bool ShowPantheonPlate => !IsCompact && HasPantheon && ChoiceDue(BuildChoices.PantheonAct, BuildChoices.PantheonMaxLevel) && !PlateHidden("pantheon");

    /// <summary>
    /// In the act of the choice, while levelling through it: an end-game character passing through the Forest Encampment
    /// (a waypoint, a league NPC) made the choice long ago.
    /// </summary>
    bool ChoiceDue(int act, int maxLevel) => (_currentAct ?? StageAct()) == act && !(_gameLevel > maxLevel);

    bool PlateHidden(string kind) =>
        Build != null && _s.PlatesHidden.TryGetValue(MarksKey, out var hidden) && hidden.Contains($"{kind}:{StageIndex}");

    public RelayCommand HidePlateCommand => _hidePlate ??= new RelayCommand(p =>
    {
        if (Build == null || p is not string kind) return;
        if (!_s.PlatesHidden.TryGetValue(MarksKey, out var hidden)) _s.PlatesHidden[MarksKey] = hidden = new HashSet<string>();
        hidden.Add($"{kind}:{StageIndex}");
        RaiseChoices();
        Save();
    });
    RelayCommand? _hidePlate;

    int? StageAct()
    {
        var m = System.Text.RegularExpressions.Regex.Match(CurrentStage?.Title ?? "", @"\b(?:act|акт)\s*(\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    void RaiseChoices()
    {
        foreach (var n in new[] { nameof(BanditText), nameof(HasBandit), nameof(PantheonText), nameof(HasPantheon), nameof(HasChoices), nameof(ShowMiniBandit),
                     nameof(ShowMiniPantheon), nameof(ChoicesLine), nameof(ShowBanditPlate), nameof(ShowPantheonPlate) })
            Raise(n);
    }

    void RaiseMini()
    {
        foreach (var n in new[] { nameof(ShowMini), nameof(ShowFullStage), nameof(ShowTownBlock), nameof(ShowMiniBandit), nameof(ShowMiniPantheon),
                     nameof(ShowBanditPlate), nameof(ShowPantheonPlate) })
            Raise(n);
    }
}

/// <summary>
/// One vendor in the town list: "Nessa · Акт 1 · 4" and its gems; "Поиск" copies the vendor window's search for just these
/// gems (several strings when long: each click the next one) and answers on itself.
/// </summary>
public sealed class VendorGroupVm : Observable
{
    readonly Func<IReadOnlyList<string>>? _build;
    readonly Func<string, bool> _copy;
    IReadOnlyList<string>? _parts;
    int _part;
    string _button = Loc.T("Поиск");
    System.Windows.Threading.DispatcherTimer? _reset;

    public VendorGroupVm(string npc, string where, IReadOnlyList<GemRowVm> gems, Func<IReadOnlyList<string>>? build, Func<string, bool> copy)
    {
        Npc = npc;
        Gems = gems;
        Meta = where.Length == 0 ? gems.Count.ToString() : $"{where} · {gems.Count}";
        _build = build;
        _copy = copy;
        SearchCommand = new RelayCommand(Search);
    }

    public string Npc { get; }
    /// <summary>"Акт 1 · 4": where the vendor stands and how many gems to buy there.</summary>
    public string Meta { get; }
    /// <summary>"Nessa · Акт 1 · 4" on the vendor's diamond heading.</summary>
    public string Heading => $"{Npc} · {Meta}";
    public IReadOnlyList<GemRowVm> Gems { get; }
    /// <summary>Lab gems are made at the Divine Font: nothing to search at a vendor.</summary>
    public bool CanSearch => _build != null;
    public string SearchButton { get => _button; private set => Set(ref _button, value); }
    public RelayCommand SearchCommand { get; }

    void Search()
    {
        if (_build == null) return;
        _parts ??= _build();
        if (_parts.Count == 0) { Flash(Loc.T("Нечего искать")); return; }
        var part = _part % _parts.Count;
        if (!_copy(_parts[part])) { Flash(Loc.T("Буфер занят, ещё раз")); return; }
        _part = part + 1;
        Flash(_parts.Count == 1 ? Loc.T("Скопировано ✓") : Loc.T($"Часть {part + 1} из {_parts.Count} ✓"));
    }

    void Flash(string text)
    {
        SearchButton = text;
        _reset ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _reset.Stop();
        _reset.Tick -= Reset;
        _reset.Tick += Reset;
        _reset.Start();
    }

    void Reset(object? sender, EventArgs e) { _reset?.Stop(); SearchButton = Loc.T("Поиск"); }
}
