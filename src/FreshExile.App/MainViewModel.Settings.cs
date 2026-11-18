using System.Collections.ObjectModel;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>One stage row in settings: start character level and start map tier (custom or read from the title).</summary>
public sealed class StageLevelVm(int index, string title, int? auto, int? custom, int? autoTier, int? customTier, Action<StageLevelVm> onChanged) : Observable
{
    string _text = custom?.ToString() ?? "";
    string _tierText = customTier?.ToString() ?? "";

    public int Index { get; } = index;
    public string Title { get; } = title;

    public int? Auto { get; } = auto;
    public string AutoText => Auto is int a ? Loc.T($"авто: {a}") : Loc.T("уровень");
    public int? Custom { get; private set; } = custom;
    public int? Effective => Custom ?? Auto;

    public int? AutoTier { get; } = autoTier;
    public string AutoTierText => AutoTier is int t ? Loc.T($"авто: T{t}") : Loc.T("тир");
    public int? CustomTier { get; private set; } = customTier;
    public int? EffectiveTier => CustomTier ?? AutoTier;

    public string Text
    {
        get => _text;
        set
        {
            var (text, v) = Parse(value, 100, Custom);
            if (!Set(ref _text, text)) return;
            Custom = v;
            onChanged(this);
        }
    }

    public string TierText
    {
        get => _tierText;
        set
        {
            var (text, v) = Parse(value.TrimStart('T', 't', 'Т', 'т'), 17, CustomTier);
            if (!Set(ref _tierText, text)) return;
            CustomTier = v;
            onChanged(this);
        }
    }

    static (string Text, int? Value) Parse(string raw, int max, int? current)
    {
        raw = raw.Trim();
        int? v = int.TryParse(raw, out var n) && n >= 1 && n <= max ? n : null;
        // garbage keeps the previous value
        return raw.Length > 0 && v == null ? (current?.ToString() ?? "", current) : (raw, v);
    }
}

public sealed partial class MainViewModel
{
    RelayCommand? _beginCapture, _resetHotkeys, _restart;

    /// <summary>Settings: the interface language; the running windows keep theirs until the restart.</summary>
    public bool IsLangRu { get => State.Language != Loc.En; set { if (value) SetLanguage(Loc.Ru); } }
    public bool IsLangEn { get => State.Language == Loc.En; set { if (value) SetLanguage(Loc.En); } }
    public bool LanguagePending => (State.Language == Loc.En ? Loc.En : Loc.Ru) != Loc.Lang;

    void SetLanguage(string lang)
    {
        if (State.Language == lang) return;
        State.Language = lang;
        Save();
        foreach (var n in new[] { nameof(IsLangRu), nameof(IsLangEn), nameof(LanguagePending) }) Raise(n);
    }

    /// <summary>Closes the overlay (state saved) and starts it again in the chosen language.</summary>
    public RelayCommand RestartCommand => _restart ??= new RelayCommand(() =>
    {
        App.RestartOnExit = true;
        System.Windows.Application.Current.MainWindow?.Close();
    });
    int _lastLevel;

    #region Level switching

    public ObservableCollection<StageLevelVm> StageLevelRows { get; } = new();

    void RebuildStageLevels()
    {
        StageLevelRows.Clear();
        if (Build == null) return;
        _s.StageLevels.TryGetValue(Build.Id, out var custom);
        _s.StageTiers.TryGetValue(Build.Id, out var tiers);
        for (var i = 0; i < Build.Stages.Count; i++)
        {
            var title = Build.Stages[i].Title;
            int? c = custom != null && custom.TryGetValue(i, out var v) ? v : null;
            int? ct = tiers != null && tiers.TryGetValue(i, out var t) ? t : null;
            StageLevelRows.Add(new StageLevelVm(i, title, StageLevels.FromTitle(title), c, StageLevels.TierFromTitle(title), ct, OnStageLevelChanged));
        }
        SyncFollower();
    }

    void OnStageLevelChanged(StageLevelVm row)
    {
        if (Build == null) return;
        Put(_s.StageLevels, row.Custom);
        Put(_s.StageTiers, row.CustomTier);
        Save();
        SyncFollower();
        if (FollowGame && _follower.ForLevel(StageIndex) is { } sw) SelectStage(sw.Stage, sw.Reason);

        void Put(Dictionary<string, Dictionary<int, int>> store, int? value)
        {
            if (!store.TryGetValue(Build!.Id, out var map)) store[Build.Id] = map = new Dictionary<int, int>();
            if (value is int v) map[row.Index] = v; else map.Remove(row.Index);
        }
    }

    /// <summary>The follower switches by what the settings rows say right now.</summary>
    void SyncFollower()
    {
        _follower.Titles = Build?.Stages.Select(s => s.Title).ToList() ?? [];
        _follower.StartLevels = StageLevelRows.Select(r => r.Effective).ToList();
        _follower.StartTiers = StageLevelRows.Select(r => r.EffectiveTier).ToList();
    }

    #endregion

    #region Settings tabs

    // the sections of the settings window, in menu order; the first, «Общие», opens by default
    enum Section { General, Tracking, Build, Tree, Trade, Price, Top, Keys, About }

    Section _section;

    void SetSection(Section s, bool on)
    {
        if (!on || _section == s) return;
        _section = s;
        foreach (var n in new[]
                 {
                     nameof(IsSetGame), nameof(IsSetTracking), nameof(IsSetBuild), nameof(IsSetTree), nameof(IsSetTrade),
                     nameof(IsSetPrice), nameof(IsSetTop), nameof(IsSetKeys), nameof(IsSetAbout), nameof(SettingsSectionTitle),
                 })
            Raise(n);
    }

    public bool IsSetGame { get => _section == Section.General; set => SetSection(Section.General, value); }
    public bool IsSetTracking { get => _section == Section.Tracking; set => SetSection(Section.Tracking, value); }
    public bool IsSetBuild { get => _section == Section.Build; set => SetSection(Section.Build, value); }
    public bool IsSetTree { get => _section == Section.Tree; set => SetSection(Section.Tree, value); }
    public bool IsSetTrade { get => _section == Section.Trade; set => SetSection(Section.Trade, value); }
    public bool IsSetPrice { get => _section == Section.Price; set => SetSection(Section.Price, value); }
    public bool IsSetTop { get => _section == Section.Top; set => SetSection(Section.Top, value); }
    public bool IsSetKeys { get => _section == Section.Keys; set => SetSection(Section.Keys, value); }
    public bool IsSetAbout { get => _section == Section.About; set => SetSection(Section.About, value); }

    /// <summary>QA: a section by its enum name, case ignored ("--settings=keys").</summary>
    public void OpenSettingsSection(string name)
    {
        if (Enum.TryParse<Section>(name, ignoreCase: true, out var s)) SetSection(s, true);
    }

    /// <summary>The heading over the open section: the same words as its menu item.</summary>
    public string SettingsSectionTitle => _section switch
    {
        Section.Tracking => Loc.T("Отслеживание"),
        Section.Build => Loc.T("Билд и этапы"),
        Section.Tree => Loc.T("Дерево"),
        Section.Trade => Loc.T("Трейд"),
        Section.Price => Loc.T("Прайс-чек"),
        Section.Top => Loc.T("Топ-моды"),
        Section.Keys => Loc.T("Клавиши"),
        Section.About => Loc.T("О программе"),
        _ => Loc.T("Общие"),
    };

    public string AppVersionText
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return Loc.T($"Версия {v?.ToString(3)}");
        }
    }

    #endregion

    #region Hotkeys

    public ObservableCollection<HotkeyRowVm> HotkeyRows { get; } = new();

    /// <summary>Raised when bindings change or capture starts/ends; the window re-registers.</summary>
    public event Action? HotkeysChanged;

    public HotkeyRowVm? CapturingRow => HotkeyRows.FirstOrDefault(r => r.IsCapturing);

    public RelayCommand BeginCaptureCommand => _beginCapture ??= new RelayCommand(p =>
    {
        if (p is not HotkeyRowVm row) return;
        foreach (var r in HotkeyRows) { r.IsCapturing = r == row; r.Error = null; }
        HotkeysChanged?.Invoke();
    });

    public RelayCommand ResetHotkeysCommand => _resetHotkeys ??= new RelayCommand(() =>
    {
        _s.Hotkeys.Clear();
        LoadHotkeys();
        Save();
        HotkeysChanged?.Invoke();
    });

    void LoadHotkeys()
    {
        if (HotkeyRows.Count == 0)
            foreach (var (id, title, _) in HotkeyActions.All) HotkeyRows.Add(new HotkeyRowVm(id, title));
        foreach (var row in HotkeyRows)
        {
            var def = HotkeyActions.All.First(a => a.Id == row.Id).Default;
            row.Binding = _s.Hotkeys.TryGetValue(row.Id, out var saved) ? HotkeyBinding.Parse(saved) : HotkeyBinding.Parse(def);
            row.IsCapturing = false;
            row.Error = null;
        }
        RaiseKeys();
    }

    /// <summary>null binding = unbind (Backspace / Delete), cancel = Escape.</summary>
    public void CompleteCapture(HotkeyBinding? binding, bool cancel = false)
    {
        var row = CapturingRow;
        if (row == null) return;
        row.IsCapturing = false;

        if (!cancel)
        {
            if (binding != null && !binding.IsSafe)
            {
                row.Error = Loc.T("Нужен Ctrl или Alt: одиночная клавиша перестанет работать в игре");
            }
            else if (binding != null && HotkeyRows.FirstOrDefault(r => r != row && r.Binding == binding) is { } other)
            {
                row.Error = Loc.T($"Уже занято: «{other.Title}»");
            }
            else
            {
                row.Binding = binding;
                _s.Hotkeys[row.Id] = binding?.Serialize() ?? "";
                Save();
                RaiseKeys();
            }
        }
        HotkeysChanged?.Invoke();
    }

    bool _reportWithFrames;

    /// <summary>Attach overlay screenshots to the report. Off on every start: chat with other players' names can be on them.</summary>
    public bool ReportWithFrames
    {
        get => _reportWithFrames;
        set => Set(ref _reportWithFrames, value);
    }

    public void ReportHotkeyFailure(string id) =>
        HotkeyRows.First(r => r.Id == id).Error = Loc.T("Занято другой программой, выбери другое сочетание");

    public string KeyText(string id) => HotkeyRows.FirstOrDefault(r => r.Id == id)?.Binding?.Display ?? Loc.T("не назначено");

    public string KeyMain => KeyText(HotkeyActions.Main);
    public string KeyTree => KeyText(HotkeyActions.Tree);
    public string KeyThrough => KeyText(HotkeyActions.Through);
    public string TipTree => Loc.T($"Дерево · {KeyTree}");
    public string TipPrev => Loc.T($"Предыдущий этап · {KeyText(HotkeyActions.Prev)}");
    public string TipNext => Loc.T($"Следующий этап · {KeyText(HotkeyActions.Next)}");
    public string TipThrough => Loc.T($"Сквозной режим: клики уходят в игру · {KeyThrough}");
    public string TipHideTree => Loc.T($"Скрыть · {KeyTree}");

    void RaiseKeys()
    {
        foreach (var n in new[] { nameof(KeyMain), nameof(KeyTree), nameof(KeyThrough), nameof(TipTree), nameof(TipPrev), nameof(TipNext), nameof(TipThrough), nameof(TipHideTree), "PriceHint" })
            Raise(n);
    }

    #endregion
}
