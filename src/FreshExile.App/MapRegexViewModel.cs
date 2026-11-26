using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>
/// The map regex window: mods to avoid or to want, the totals at least so much, the map's state; the string for the
/// stash / map device search updates as you click. Everything picked is kept in <see cref="AppState.MapRegex"/>.
/// </summary>
public sealed class MapRegexViewModel : Observable
{
    readonly MapRegexState _s;
    readonly Action _save;
    bool _loading;

    public ObservableCollection<MapModRowVm> Rows { get; } = new();
    public ICollectionView View { get; }
    public IReadOnlyList<MapTotalVm> Totals { get; }
    public ObservableCollection<MapPresetVm> Presets { get; } = new();

    public MapRegexViewModel(MapRegexState state, Action save)
    {
        _s = state;
        _save = save;
        foreach (var m in MapRegex.Mods) Rows.Add(new MapModRowVm(m, Changed));
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is MapModRowVm r && Shows(r);
        Totals =
        [
            // the property block's names, short: "Item Quantity", "More Currency"…
            new("Quantity", Loc.T("Количество"), Changed),
            new("Rarity", Loc.T("Редкость"), Changed),
            new("PackSize", Loc.T("Размер групп"), Changed),
            new("Drops", "Atlas Drops", Changed),
            new("Currency", Loc.T("Валюта"), Changed),
            new("Scarabs", Loc.T("Скарабеи"), Changed),
            new("Maps", Loc.T("Карты"), Changed),
            new("Cards", Loc.T("Гадальные"), Changed),
            new("Quality", Loc.T("Качество"), Changed),
        ];
        foreach (var p in _s.Presets) Presets.Add(new MapPresetVm(p));
        Apply(_s);
    }

    #region What is asked

    string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) View.Refresh(); }
    }

    /// <summary>0 = every mod, 1 = only those on Tier 1-16 maps, 2 = only Tier 17 ones.</summary>
    int _tier;
    public bool TierAll { get => _tier == 0; set { if (value) SetTier(0); } }
    public bool TierRegular { get => _tier == 1; set { if (value) SetTier(1); } }
    public bool TierUber { get => _tier == 2; set { if (value) SetTier(2); } }

    void SetTier(int t)
    {
        _tier = t;
        Raise(nameof(TierAll)); Raise(nameof(TierRegular)); Raise(nameof(TierUber));
        View.Refresh();
    }

    bool _pickedOnly;
    /// <summary>Only the rows already picked: what the string is made of.</summary>
    public bool PickedOnly
    {
        get => _pickedOnly;
        set { if (Set(ref _pickedOnly, value)) View.Refresh(); }
    }

    bool Shows(MapModRowVm r)
    {
        if (_pickedOnly && !r.IsAvoid && !r.IsWant) return false;
        if (_tier == 1 && !r.Mod.Regular) return false;
        if (_tier == 2 && !r.Mod.Uber) return false;
        return _search.Length == 0 || r.Mod.Text.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    bool _wantAll;
    public bool WantAll { get => _wantAll; set { if (Set(ref _wantAll, value)) { Raise(nameof(WantAny)); Changed(); } } }
    public bool WantAny { get => !_wantAll; set => WantAll = !value; }

    bool? _corrupted, _unid;
    public bool? Corrupted { get => _corrupted; set { if (Set(ref _corrupted, value)) Changed(); } }
    public bool? Unidentified { get => _unid; set { if (Set(ref _unid, value)) Changed(); } }

    bool _normal, _magic, _rare;
    public bool Normal { get => _normal; set { if (Set(ref _normal, value)) Changed(); } }
    public bool Magic { get => _magic; set { if (Set(ref _magic, value)) Changed(); } }
    public bool Rare { get => _rare; set { if (Set(ref _rare, value)) Changed(); } }

    #endregion

    #region The string

    string _result = "";
    public string Result { get => _result; private set => Set(ref _result, value); }
    public string LengthText => $"{_result.Length}/{MapRegex.MaxLength}";
    public bool TooLong => _result.Length > MapRegex.MaxLength;
    public bool HasResult => _result.Length > 0;
    public int AvoidCount => Rows.Count(r => r.IsAvoid);
    public int WantCount => Rows.Count(r => r.IsWant);
    public string AvoidCaption => AvoidCount > 0 ? Loc.T($"Избегать · {AvoidCount}") : Loc.T("Избегать");
    public string WantCaption => WantCount > 0 ? Loc.T($"Нужны · {WantCount}") : Loc.T("Нужны");

    void Changed()
    {
        if (_loading) return;
        var q = new MapRegexQuery
        {
            Avoid = Rows.Where(r => r.IsAvoid).Select(r => r.Mod.Id).ToHashSet(),
            Want = Rows.Where(r => r.IsWant).Select(r => r.Mod.Id).ToHashSet(),
            WantAll = _wantAll, Corrupted = _corrupted, Unidentified = _unid, Normal = _normal, Magic = _magic, Rare = _rare,
        };
        foreach (var t in Totals)
        {
            var v = t.Number;
            switch (t.Key)
            {
                case "Quantity": q.Quantity = v; break;
                case "Rarity": q.Rarity = v; break;
                case "PackSize": q.PackSize = v; break;
                case "Drops": q.Drops = v; break;
                case "Currency": q.Currency = v; break;
                case "Scarabs": q.Scarabs = v; break;
                case "Maps": q.Maps = v; break;
                case "Cards": q.Cards = v; break;
                case "Quality": q.Quality = v; break;
            }
        }
        Result = MapRegex.Build(q);
        Status = "";
        foreach (var n in new[] { nameof(LengthText), nameof(TooLong), nameof(HasResult), nameof(AvoidCount), nameof(WantCount), nameof(AvoidCaption), nameof(WantCaption) })
            Raise(n);
        if (_pickedOnly) View.Refresh();
        Store(_s);
        _save();
    }

    /// <summary>The answer to the last button: "Скопировано…", "Набор сохранён"; gone with the next change.</summary>
    string _status = "";
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
    void Say(string text) => Status = text;

    public RelayCommand CopyCommand => _copy ??= new RelayCommand(() =>
        Say(_result.Length == 0 ? Loc.T("Строка пустая: отметь моды или числа")
            : MainViewModel.TrySetClipboard(_result) ? TooLong ? Loc.T("Скопировано, но длиннее 250 символов: игра обрежет") : Loc.T("Скопировано: вставь в поиск тайника или устройства карт")
            : Loc.T("Буфер занят, ещё раз")));
    RelayCommand? _copy;

    public RelayCommand ResetCommand => _reset ??= new RelayCommand(() => Apply(new MapRegexState()));
    RelayCommand? _reset;

    #endregion

    #region Presets

    string _presetName = "";
    public string PresetName { get => _presetName; set => Set(ref _presetName, value); }

    public RelayCommand SavePresetCommand => _savePreset ??= new RelayCommand(() =>
    {
        var name = _presetName.Trim();
        if (name.Length == 0) { Say(Loc.T("Назови набор")); return; }
        var p = new MapRegexState { Name = name };
        Store(p);
        _s.Presets.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        _s.Presets.Add(p);
        Presets.Clear();
        foreach (var x in _s.Presets) Presets.Add(new MapPresetVm(x));
        PresetName = "";
        _save();
        Say(Loc.T($"Набор «{name}» сохранён"));
    });
    RelayCommand? _savePreset;

    public RelayCommand LoadPresetCommand => _loadPreset ??= new RelayCommand(p => { if (p is MapPresetVm vm) Apply(vm.State); });
    RelayCommand? _loadPreset;

    public RelayCommand DeletePresetCommand => _deletePreset ??= new RelayCommand(p =>
    {
        if (p is not MapPresetVm vm) return;
        _s.Presets.Remove(vm.State);
        Presets.Remove(vm);
        _save();
    });
    RelayCommand? _deletePreset;

    #endregion

    void Store(MapRegexState to)
    {
        to.Avoid = Rows.Where(r => r.IsAvoid).Select(r => r.Mod.ToString()).ToList();
        to.Want = Rows.Where(r => r.IsWant).Select(r => r.Mod.ToString()).ToList();
        to.WantAll = _wantAll;
        to.Totals = Totals.Where(t => t.Number != null).ToDictionary(t => t.Key, t => t.Number!.Value);
        to.Corrupted = _corrupted;
        to.Unidentified = _unid;
        to.Normal = _normal;
        to.Magic = _magic;
        to.Rare = _rare;
    }

    void Apply(MapRegexState from)
    {
        _loading = true;
        try
        {
            var avoid = from.Avoid.ToHashSet(StringComparer.Ordinal);
            var want = from.Want.ToHashSet(StringComparer.Ordinal);
            foreach (var r in Rows) r.Set(avoid.Contains(r.Mod.ToString()), want.Contains(r.Mod.ToString()));
            foreach (var t in Totals) t.Text = from.Totals.TryGetValue(t.Key, out var v) ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            WantAll = from.WantAll;
            Corrupted = from.Corrupted;
            Unidentified = from.Unidentified;
            Normal = from.Normal;
            Magic = from.Magic;
            Rare = from.Rare;
        }
        finally { _loading = false; }
        Changed();
    }
}

/// <summary>A mod row: its lines, the Tier 17 mark, two toggles that exclude each other.</summary>
public sealed class MapModRowVm(MapMod mod, Action changed) : Observable
{
    public MapMod Mod { get; } = mod;
    public string Text => Mod.Text;
    public bool IsUber => Mod.Uber && !Mod.Regular;
    public string KindText => Mod.Prefix && !Mod.Suffix ? Loc.T("префикс") : Mod.Suffix && !Mod.Prefix ? Loc.T("суффикс") : "";
    /// <summary>The piece of text that stands for the mod in the string, and the mods it catches as well.</summary>
    public string Tip => Mod.AlsoMatches.Count == 0
        ? Loc.T($"В строке: «{Mod.Token}»")
        : Loc.T($"В строке: «{Mod.Token}». Заодно ловит: {string.Join("; ", Mod.AlsoMatches.Select(m => m.ToString()))}");

    bool _avoid, _want;
    public bool IsAvoid
    {
        get => _avoid;
        set
        {
            if (!Set(ref _avoid, value)) return;
            if (value && _want) { _want = false; Raise(nameof(IsWant)); }
            Raise(nameof(IsPicked));
            changed();
        }
    }
    public bool IsWant
    {
        get => _want;
        set
        {
            if (!Set(ref _want, value)) return;
            if (value && _avoid) { _avoid = false; Raise(nameof(IsAvoid)); }
            Raise(nameof(IsPicked));
            changed();
        }
    }
    public bool IsPicked => _avoid || _want;

    /// <summary>From a saved state: no change notice per row.</summary>
    internal void Set(bool avoid, bool want)
    {
        _avoid = avoid;
        _want = want && !avoid;
        Raise(nameof(IsAvoid)); Raise(nameof(IsWant)); Raise(nameof(IsPicked));
    }
}

/// <summary>A total of the property block: "Количество предметов ≥ 80".</summary>
public sealed class MapTotalVm(string key, string label, Action changed) : Observable
{
    public string Key { get; } = key;
    public string Label { get; } = label;

    string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            var digits = new string((value ?? "").Where(char.IsAsciiDigit).Take(3).ToArray());
            if (!Set(ref _text, digits)) return;
            changed();
        }
    }
    public int? Number => int.TryParse(_text, out var n) && n > 0 ? n : null;
}

public sealed class MapPresetVm(MapRegexState state)
{
    public MapRegexState State { get; } = state;
    public string Name => State.Name;
}
