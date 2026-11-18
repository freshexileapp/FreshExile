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
        Raise(nameof(ShowMiniBandit));
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
    public bool ShowMiniBandit => ShowMini && HasBandit && (_currentAct ?? StageAct()) == 2;

    int? StageAct()
    {
        var m = System.Text.RegularExpressions.Regex.Match(StageTitle ?? "", @"\b(?:act|акт)\s*(\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    void RaiseChoices()
    {
        foreach (var n in new[] { nameof(BanditText), nameof(HasBandit), nameof(PantheonText), nameof(HasPantheon), nameof(HasChoices), nameof(ShowMiniBandit) })
            Raise(n);
    }

    void RaiseMini()
    {
        foreach (var n in new[] { nameof(ShowMini), nameof(ShowFullStage), nameof(ShowTownBlock), nameof(ShowMiniBandit) })
            Raise(n);
    }
}
