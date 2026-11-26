using System.Collections.ObjectModel;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>One stage row in settings: start character level and start map tier (custom or read from the title).</summary>
public sealed class StageLevelVm(int index, string title, int? auto, int? custom, int? autoTier, int? customTier, Action<StageLevelVm> onChanged,
    string? name = null, Action<StageLevelVm>? onRenamed = null) : Observable
{
    string _name = name ?? "";

    /// <summary>The player's own name for the stage; empty = PoB's <see cref="Title"/> (shown as the box's placeholder).</summary>
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value ?? "")) onRenamed?.Invoke(this); }
    }

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

    /// <summary>The scale choices of Settings → General: "90 %" … "150 %", the current one checked.</summary>
    public IReadOnlyList<ScaleOption> ScaleOptions => _scaleOptions ??= UiScale.Steps.Select(s => new ScaleOption(this, s)).ToList();
    IReadOnlyList<ScaleOption>? _scaleOptions;

    public void SetUiScale(double factor)
    {
        if (Math.Abs(State.UiScale - factor) < 0.001) return;
        State.UiScale = factor;
        UiScale.Set(factor);
        foreach (var o in ScaleOptions) o.Refresh();
        Save();
    }

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
        _s.StageLevels.TryGetValue(MarksKey, out var custom);
        _s.StageTiers.TryGetValue(MarksKey, out var tiers);
        _s.StageNames.TryGetValue(MarksKey, out var names);
        for (var i = 0; i < Build.Stages.Count; i++)
        {
            var title = Build.Stages[i].Title;
            int? c = custom != null && custom.TryGetValue(i, out var v) ? v : null;
            int? ct = tiers != null && tiers.TryGetValue(i, out var t) ? t : null;
            string? name = names != null && names.TryGetValue(i, out var nm) ? nm : null;
            StageLevelRows.Add(new StageLevelVm(i, title, StageLevels.FromTitle(title), c, StageLevels.TierFromTitle(title), ct, OnStageLevelChanged, name, OnStageRenamed));
        }
        SyncFollower();
    }

    /// <summary>A stage's name as shown: the player's own when given, else PoB's title.</summary>
    public string StageName(int i)
    {
        if (Build == null || i < 0 || i >= Build.Stages.Count) return "";
        return _s.StageNames.TryGetValue(MarksKey, out var map) && map.TryGetValue(i, out var n) && n.Length > 0 ? n : Build.Stages[i].Title;
    }

    /// <summary>A name typed in the settings row: kept per build, shown on the panel right away; empty = back to PoB's.</summary>
    void OnStageRenamed(StageLevelVm row)
    {
        if (Build == null) return;
        if (!_s.StageNames.TryGetValue(MarksKey, out var map)) _s.StageNames[MarksKey] = map = new Dictionary<int, string>();
        var name = row.Name.Trim();
        if (name.Length == 0 || name == row.Title) map.Remove(row.Index); else map[row.Index] = name;
        if (map.Count == 0) _s.StageNames.Remove(MarksKey);
        if (row.Index < Stages.Count) Stages[row.Index].Title = StageName(row.Index);
        Raise(nameof(StageTitle));
        Raise(nameof(SwapCaption));
        Save();
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
            if (!store.TryGetValue(MarksKey, out var map)) store[MarksKey] = map = new Dictionary<int, int>();
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
    // «Трейд» and «Прайс-чек» are one section now: the search on the site, then the price check window
    enum Section { General, Tracking, Build, Tree, Trade, Top, Keys, About }

    Section _section;

    void SetSection(Section s, bool on)
    {
        if (!on || _section == s) return;
        _section = s;
        foreach (var n in new[]
                 {
                     nameof(IsSetGame), nameof(IsSetTracking), nameof(IsSetBuild), nameof(IsSetTree), nameof(IsSetTrade),
                     nameof(IsSetTop), nameof(IsSetKeys), nameof(IsSetAbout), nameof(SettingsSectionTitle),
                 })
            Raise(n);
    }

    public bool IsSetGame { get => _section == Section.General; set => SetSection(Section.General, value); }
    public bool IsSetTracking { get => _section == Section.Tracking; set => SetSection(Section.Tracking, value); }
    public bool IsSetBuild { get => _section == Section.Build; set => SetSection(Section.Build, value); }
    public bool IsSetTree { get => _section == Section.Tree; set => SetSection(Section.Tree, value); }
    public bool IsSetTrade { get => _section == Section.Trade; set => SetSection(Section.Trade, value); }
    public bool IsSetTop { get => _section == Section.Top; set => SetSection(Section.Top, value); }
    public bool IsSetKeys { get => _section == Section.Keys; set => SetSection(Section.Keys, value); }
    public bool IsSetAbout { get => _section == Section.About; set => SetSection(Section.About, value); }

    /// <summary>The menu's "обновление скачано ›": to «О программе».</summary>
    public RelayCommand OpenAboutCommand => _openAbout ??= new RelayCommand(() => SetSection(Section.About, true));
    RelayCommand? _openAbout;

    /// <summary>QA: a section by its enum name, case ignored ("--settings=keys").</summary>
    public void OpenSettingsSection(string name)
    {
        // "price" was a section of its own: it is the second half of «Трейд и прайс-чек» now
        if (string.Equals(name, "price", StringComparison.OrdinalIgnoreCase)) name = nameof(Section.Trade);
        if (Enum.TryParse<Section>(name, ignoreCase: true, out var s)) SetSection(s, true);
    }

    /// <summary>The heading over the open section: the same words as its menu item.</summary>
    public string SettingsSectionTitle => _section switch
    {
        Section.Tracking => Loc.T("Отслеживание"),
        Section.Build => Loc.T("Билд и этапы"),
        Section.Tree => Loc.T("Дерево"),
        Section.Trade => Loc.T("Трейд и прайс-чек"),
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

    /// <summary>The row's own default back (the ↺ beside a changed key).</summary>
    public RelayCommand ResetHotkeyCommand => _resetHotkey ??= new RelayCommand(p =>
    {
        if (p is not HotkeyRowVm row) return;
        _s.Hotkeys.Remove(row.Id);
        row.Binding = HotkeyBinding.Parse(HotkeyActions.All.First(a => a.Id == row.Id).Default);
        row.Error = null;
        Save();
        RaiseKeys();
        HotkeysChanged?.Invoke();
    });
    RelayCommand? _resetHotkey;

    /// <summary>Combinations Windows or every program already owns: copy, paste, closing and switching windows.</summary>
    static string? Reserved(HotkeyBinding b)
    {
        var ctrl = b.Mods == System.Windows.Input.ModifierKeys.Control;
        var alt = b.Mods == System.Windows.Input.ModifierKeys.Alt;
        if (ctrl && b.Key is System.Windows.Input.Key.C or System.Windows.Input.Key.V or System.Windows.Input.Key.X or System.Windows.Input.Key.Z)
            return Loc.T("Это копирование и вставка в игре и в Windows: выбери другое сочетание");
        if (alt && b.Key is System.Windows.Input.Key.F4 or System.Windows.Input.Key.Tab)
            return Loc.T("Это сочетание Windows (закрыть окно, переключить окно): выбери другое");
        if (b.Mods == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt) && b.Key == System.Windows.Input.Key.Delete)
            return Loc.T("Это сочетание Windows: выбери другое");
        return null;
    }

    /// <summary>Two actions on one key: both rows say so (kept, but only one of them will work).</summary>
    void MarkHotkeyClashes()
    {
        foreach (var row in HotkeyRows)
        {
            var other = row.Binding == null ? null : HotkeyRows.FirstOrDefault(r => r != row && r.Binding == row.Binding);
            row.Clash = other == null ? null : Loc.T($"Занято: {other.Title}");
            row.IsDefault = row.Binding == HotkeyBinding.Parse(HotkeyActions.All.First(a => a.Id == row.Id).Default);
        }
    }

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
            else if (binding != null && Reserved(binding) is { } why)
            {
                row.Error = why;
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
    public string TipPrev => Loc.T($"Предыдущий этап · {KeyText(HotkeyActions.Prev)}");
    public string TipNext => Loc.T($"Следующий этап · {KeyText(HotkeyActions.Next)}");
    public string TipThrough => Loc.T($"Сквозной режим: клики уходят в игру · {KeyThrough}");
    public string TipHideTree => Loc.T($"Скрыть · {KeyTree}");
    public string TipMerc => Loc.T($"Прайс-чек наёмника: обведи его окно в игре мышкой. Потом то же место читает {KeyText(HotkeyActions.Merc)}");

    void RaiseKeys()
    {
        MarkHotkeyClashes();
        foreach (var n in new[] { nameof(KeyMain), nameof(KeyTree), nameof(KeyThrough), nameof(TipPrev), nameof(TipNext), nameof(TipThrough), nameof(TipHideTree), nameof(TipMerc), "PriceHint" })
            Raise(n);
    }

    #endregion
}

/// <summary>One choice of the interface scale: its label and whether it is the one in use.</summary>
public sealed class ScaleOption(MainViewModel vm, double factor) : Observable
{
    public string Label { get; } = $"{Math.Round(factor * 100)} %";
    public bool IsChecked
    {
        get => Math.Abs(vm.State.UiScale - factor) < 0.001;
        set { if (value) vm.SetUiScale(factor); }
    }
    public void Refresh() => Raise(nameof(IsChecked));
}
