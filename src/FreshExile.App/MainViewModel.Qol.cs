using System.Windows;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>Vendor search string, a new character in the log, hiding while the game is not in front.</summary>
public sealed partial class MainViewModel
{
    #region Vendor search

    IReadOnlyList<string>? _vendorSearch;
    int _vendorSearchPart;
    RelayCommand? _copyVendorSearch;

    /// <summary>
    /// Regex of the gems still to buy for the vendor window search. More than 50 characters: several strings,
    /// each click copies the next one.
    /// </summary>
    public RelayCommand CopyVendorSearchCommand => _copyVendorSearch ??= new RelayCommand(CopyVendorSearch);

    static readonly string VendorSearchIdle = Loc.T("Поиск для торговца");
    string _vendorSearchButton = VendorSearchIdle;
    System.Windows.Threading.DispatcherTimer? _vendorSearchReset;

    /// <summary>The button answers on itself ("Скопировано ✓"): a toast would cover the small town list.</summary>
    public string VendorSearchButton
    {
        get => _vendorSearchButton;
        private set => Set(ref _vendorSearchButton, value);
    }

    void FlashVendorSearchButton(string text)
    {
        VendorSearchButton = text;
        _vendorSearchReset ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _vendorSearchReset.Stop();
        _vendorSearchReset.Tick -= ResetVendorSearchButton;
        _vendorSearchReset.Tick += ResetVendorSearchButton;
        _vendorSearchReset.Start();
    }

    void ResetVendorSearchButton(object? sender, EventArgs e)
    {
        _vendorSearchReset?.Stop();
        VendorSearchButton = VendorSearchIdle;
    }

    void CopyVendorSearch()
    {
        if (TownGems.Count == 0) { FlashVendorSearchButton(Loc.T("Покупать нечего")); return; }
        if (_vendorSearch == null)
        {
            // a lab gem is not on the vendor; its base name would only find the gem already owned
            var names = TownGems.Where(r => !r.FromLab).Select(r => _gems?.Find(r.Gem)?.Name ?? r.Name);
            _vendorSearch = VendorSearch.Build(names, _gems?.Names ?? names);
            _vendorSearchPart = 0;
        }
        if (_vendorSearch.Count == 0) { FlashVendorSearchButton(Loc.T("Остались только гемы из лабы")); return; }
        var part = _vendorSearchPart % _vendorSearch.Count;
        var text = _vendorSearch[part];
        if (!TrySetClipboard(text)) { FlashVendorSearchButton(Loc.T("Буфер занят, ещё раз")); return; }
        _vendorSearchPart = part + 1;
        FlashVendorSearchButton(_vendorSearch.Count == 1 ? Loc.T("Скопировано ✓") : Loc.T($"Часть {part + 1} из {_vendorSearch.Count} ✓"));
    }

    internal static bool TrySetClipboard(string text)
    {
        // another program can hold the clipboard for a moment
        for (var i = 0; i < 3; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
        return false;
    }

    #endregion

    #region New character

    string? _newCharName, _dismissedNewChar;
    RelayCommand? _acceptNewChar, _dismissNewChar;

    public bool HasNewCharPrompt => _newCharName != null;
    public string NewCharText => _newCharName is { Length: > 0 } n
        ? Loc.T($"Новый персонаж {n}? Начать билд с этапа 1")
        : Loc.T("Похоже, новый персонаж. Начать билд с этапа 1?");

    public RelayCommand AcceptNewCharCommand => _acceptNewChar ??= new RelayCommand(AcceptNewCharacter);
    public RelayCommand DismissNewCharCommand => _dismissNewChar ??= new RelayCommand(DismissNewCharacter);

    bool _adoptNextName;

    void OfferNewCharacter(string name)
    {
        // "same build" was answered on the nameless Twilight Strand guess: its name, once known, is just taken
        if (_adoptNextName && name.Length > 0)
        {
            _adoptNextName = false;
            Character = name;
            return;
        }
        if (name == _dismissedNewChar || (name.Length > 0 && _newCharName == name)) return;
        // a level-up with the name beats the nameless Twilight Strand guess
        if (_newCharName is { Length: > 0 } && name.Length == 0) return;
        _newCharName = name;
        RaiseNewChar();
    }

    /// <summary>Fresh start: stage 1, nothing bought, the old character's profile and tree no longer apply.</summary>
    void AcceptNewCharacter()
    {
        if (Build == null || _newCharName == null) return;
        var name = _newCharName;
        _newCharName = null;

        BoughtSet.Clear();
        _char = null;
        ClearLive();
        CharStatus = null;
        CharError = null;
        // empty name: the next level-up line fills it in
        Character = name;
        CompareWithCharacter = false;
        // nothing of any stage is taken yet: the path from the class start on every stage, taken = seen gold
        State.FreshCharacter = true;
        if (StageIndex == 0) RefreshStage();
        else SelectStage(0);
        AlignNodesChanged?.Invoke();
        Save();
        RaiseNewChar();
        Toast(Loc.T("Этап 1, отметки «куплено» сброшены"));
    }

    /// <summary>The guide goes on as it is and follows this character from now on, without asking again.</summary>
    void DismissNewCharacter()
    {
        if (_newCharName is { Length: > 0 } name) Character = name;
        else _adoptNextName = true;
        _dismissedNewChar = _newCharName;
        _newCharName = null;
        RaiseNewChar();
    }

    /// <summary>QA: "--demo-newchar" shows the prompt as if another character levelled up.</summary>
    public void DemoNewCharacter() => OfferNewCharacter("AltRanger");

    void RaiseNewChar()
    {
        Raise(nameof(HasNewCharPrompt));
        Raise(nameof(NewCharText));
    }

    #endregion

    #region Hide while the game is not in front

    public bool HideWhenGameInactive
    {
        get => _s.HideWhenGameInactive;
        set { if (_s.HideWhenGameInactive == value) return; _s.HideWhenGameInactive = value; Raise(); Save(); }
    }

    #endregion
}
