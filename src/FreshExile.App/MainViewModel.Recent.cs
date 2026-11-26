using System.Collections.ObjectModel;
using System.Globalization;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>A build in the recent list: what it is, where it came from, how far it went.</summary>
public sealed class RecentBuildVm(RecentBuild entry, bool current) : Observable
{
    bool _renaming;
    string _editName = "";

    public RecentBuild Entry { get; } = entry;

    /// <summary>The build open right now: marked, not opened again, not removed.</summary>
    public bool IsCurrent { get; } = current;
    public bool CanOpen => !IsCurrent;

    public bool IsRenamed => !string.IsNullOrWhiteSpace(Entry.Name);

    /// <summary>"Ranger · Pathfinder" out of the stored "Ranger · Pathfinder, ур. 92".</summary>
    public string ClassLine => Entry.Title.Split(", ", 2)[0];
    /// <summary>"ур. 92", empty when PoB had no level.</summary>
    public string LevelText => Entry.Title.Split(", ", 2) is [_, var lvl] ? lvl : "";

    /// <summary>The player's own name, else the class line: never the class glued to a name.</summary>
    public string Title => IsRenamed ? Entry.Name! : ClassLine;

    /// <summary>"Ranger · Pathfinder · ур. 92 · этап 3 из 7 · 19.11": the class only when the title is the player's name.</summary>
    public string Details => string.Join(" · ", new[]
    {
        IsRenamed ? ClassLine : null,
        LevelText,
        Entry.Stages > 1 ? Loc.T($"этап {Entry.StageIndex + 1} из {Entry.Stages}") : null,
        Entry.At.ToLocalTime().ToString("dd.MM", CultureInfo.InvariantCulture),
    }.Where(x => !string.IsNullOrEmpty(x)));

    /// <summary>Loaded from a link (shown as a chain icon, the link in its tooltip); a pasted code shows nothing.</summary>
    public bool HasLink => Entry.Input.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    public string? LinkTip => HasLink ? Entry.Input : null;

    public bool IsPinned => Entry.Pinned;
    /// <summary>Segoe Fluent star: filled when pinned.</summary>
    public string PinGlyph => Entry.Pinned ? "" : "";
    public string PinTip => Entry.Pinned ? Loc.T("Открепить: снова может уйти из списка") : Loc.T("Закрепить: всегда сверху, новые билды не вытеснят");

    public bool IsRenaming { get => _renaming; set { if (Set(ref _renaming, value)) Raise(nameof(IsNotRenaming)); } }
    public bool IsNotRenaming => !_renaming;
    public string EditName { get => _editName; set => Set(ref _editName, value); }

}

public sealed partial class MainViewModel
{
    /// <summary>Unpinned builds kept; pinned ones come on top of these.</summary>
    const int RecentMax = 8;

    /// <summary>Builds loaded before, pinned first, the open one marked: one click brings one back, at the stage it was left on.</summary>
    public ObservableCollection<RecentBuildVm> RecentBuilds { get; } = new();
    public bool HasRecentBuilds => RecentBuilds.Count > 0;

    /// <summary>The open build as a card of its own, above the lists (never a line among them).</summary>
    public RecentBuildVm? CurrentRecent => RecentBuilds.FirstOrDefault(r => r.IsCurrent);
    public bool HasCurrentRecent => CurrentRecent != null;
    /// <summary>The other builds: pinned ones, then the rest.</summary>
    public IReadOnlyList<RecentBuildVm> PinnedRecent => RecentBuilds.Where(r => r.IsPinned && !r.IsCurrent).ToList();
    public IReadOnlyList<RecentBuildVm> OtherRecent => RecentBuilds.Where(r => !r.IsPinned && !r.IsCurrent).ToList();
    public bool HasPinnedRecent => PinnedRecent.Count > 0;
    public bool HasOtherRecent => OtherRecent.Count > 0;
    /// <summary>"Очистить" has something to take: a build neither pinned nor open.</summary>
    public bool CanClearRecent => RecentBuilds.Any(r => !r.IsPinned && !r.IsCurrent);

    public RelayCommand OpenRecentCommand => _openRecent ??= new RelayCommand(p => { if (p is RecentBuildVm { IsCurrent: false } r) OpenRecent(r.Entry); }, _ => !IsBusy);
    RelayCommand? _openRecent;

    public RelayCommand RemoveRecentCommand => _removeRecent ??= new RelayCommand(p =>
    {
        if (p is not RecentBuildVm { IsCurrent: false } r) return;
        _s.RecentBuilds.Remove(r.Entry);
        RefreshRecent();
        Save();
    });
    RelayCommand? _removeRecent;

    public RelayCommand PinRecentCommand => _pinRecent ??= new RelayCommand(p =>
    {
        if (p is not RecentBuildVm r) return;
        r.Entry.Pinned = !r.Entry.Pinned;
        TrimRecent();
        RefreshRecent();
        Save();
    });
    RelayCommand? _pinRecent;

    /// <summary>Every build that is neither pinned nor open leaves the list (after "Да" in the line's own question).</summary>
    public RelayCommand ClearRecentCommand => _clearRecent ??= new RelayCommand(() =>
    {
        ConfirmingClear = false;
        _s.RecentBuilds.RemoveAll(r => !r.Pinned && !IsOpen(r));
        RefreshRecent();
        Save();
    });

    bool _confirmingClear;
    System.Windows.Threading.DispatcherTimer? _confirmClearTimer;

    /// <summary>"Очистить" turned into "Точно очистить? Да · Нет" for 4 s.</summary>
    public bool ConfirmingClear
    {
        get => _confirmingClear;
        set
        {
            if (!Set(ref _confirmingClear, value)) return;
            Raise(nameof(NotConfirmingClear));
            _confirmClearTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _confirmClearTimer.Stop();
            _confirmClearTimer.Tick -= ConfirmClearTimeout;
            _confirmClearTimer.Tick += ConfirmClearTimeout;
            if (value) _confirmClearTimer.Start();
        }
    }
    public bool NotConfirmingClear => !_confirmingClear;
    void ConfirmClearTimeout(object? sender, EventArgs e) => ConfirmingClear = false;

    public RelayCommand AskClearRecentCommand => _askClear ??= new RelayCommand(() => ConfirmingClear = true);
    public RelayCommand CancelClearRecentCommand => _cancelClear ??= new RelayCommand(() => ConfirmingClear = false);
    RelayCommand? _askClear, _cancelClear;
    RelayCommand? _clearRecent;

    /// <summary>The line whose name is being typed; Enter / Esc in the settings window go to it.</summary>
    public RecentBuildVm? RenamingRecent { get; private set; }

    public RelayCommand RenameRecentCommand => _renameRecent ??= new RelayCommand(p =>
    {
        if (p is not RecentBuildVm r) return;
        if (RenamingRecent is { } other && other != r) CommitRename();
        // the commit (here, or on the box's lost focus) may have rebuilt the lines: the one now on screen
        r = RecentBuilds.FirstOrDefault(x => x.Entry == r.Entry) ?? r;
        r.EditName = r.Title;
        r.IsRenaming = true;
        RenamingRecent = r;
    });
    RelayCommand? _renameRecent;

    /// <summary>The typed name kept (and the build pinned: a named build is one to keep); empty = back to the PoB title.</summary>
    public void CommitRename()
    {
        if (RenamingRecent is not { } r) return;
        RenamingRecent = null;
        r.IsRenaming = false;
        var name = r.EditName.Trim();
        var named = name.Length > 0 && name != r.Entry.Title;
        if (named == r.IsRenamed && (!named || name == r.Entry.Name)) return;
        r.Entry.Name = named ? name : null;
        if (named) r.Entry.Pinned = true;
        RefreshRecent();
        Save();
    }

    public void CancelRename()
    {
        if (RenamingRecent is not { } r) return;
        RenamingRecent = null;
        r.IsRenaming = false;
    }

    /// <summary>
    /// The current build goes to the top of the list (or replaces its older self): the same build key (<see cref="MarksKey"/>:
    /// a chain of saved stages that grew keeps it), the same XML, or the same link — a link whose PoB changed is still the
    /// same build. Not <see cref="Build.Id"/>: two different one-tree Pathfinder builds share it, and the new one wiped the
    /// old one. Its name and pin stay.
    /// </summary>
    void RememberBuild()
    {
        if (Build == null || string.IsNullOrEmpty(_s.BuildXml) || _s.BuildId == null) return;
        var input = _s.BuildInput.Trim();
        var link = input.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        bool Same(RecentBuild r) => IsOpen(r) || r.Xml == _s.BuildXml
                                    || (link && string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase));
        var old = _s.RecentBuilds.FirstOrDefault(Same);
        _s.RecentBuilds.RemoveAll(Same);
        _s.RecentBuilds.Insert(0, new RecentBuild
        {
            Id = _s.BuildId,
            Title = BuildLine + (Build.Level > 0 ? Loc.T($", ур. {Build.Level}") : ""),
            Name = old?.Name,
            Pinned = old?.Pinned ?? false,
            // a pasted code is the build itself: kept as XML below, not twice
            Input = link ? input : "",
            Xml = _s.BuildXml,
            StageIndex = _s.StageIndex,
            Stages = Build.Stages.Count,
            At = DateTime.UtcNow,
        });
        TrimRecent();
        RefreshRecent();
    }

    /// <summary>The oldest unpinned builds beyond <see cref="RecentMax"/> go; pinned ones never do.</summary>
    void TrimRecent()
    {
        var unpinned = 0;
        _s.RecentBuilds.RemoveAll(r => !r.Pinned && ++unpinned > RecentMax);
    }

    /// <summary>Before another build takes over: the stage this one was left on.</summary>
    void KeepStageOfCurrent()
    {
        if (_s.RecentBuilds.FirstOrDefault(IsOpen) is { } cur) cur.StageIndex = _s.StageIndex;
    }

    void OpenRecent(RecentBuild entry)
    {
        Build build;
        try { build = BuildParser.Parse(entry.Xml); }
        catch (PobFormatException)
        {
            _s.RecentBuilds.Remove(entry);
            RefreshRecent();
            Toast(Loc.T("Этот билд больше не читается — убрал из списка"));
            Save();
            return;
        }
        KeepStageOfCurrent();
        _s.BuildXml = entry.Xml;
        _s.BuildId = string.IsNullOrEmpty(entry.Id) ? NewBuildKey() : entry.Id;
        _s.BuildInput = entry.Input;
        _s.StageIndex = Math.Clamp(entry.StageIndex, 0, Math.Max(0, build.Stages.Count - 1));
        BuildInput = entry.Input;
        ApplyBuild(build);
        _tab = Tab.Gems;
        RaisePages();
        RememberBuild();
        Toast(Loc.T($"Открыт билд · {(string.IsNullOrWhiteSpace(entry.Name) ? BuildLine : entry.Name)}"));
        Save();
    }

    /// <summary>The open build's line: by its key, one per loaded build.</summary>
    bool IsOpen(RecentBuild r) => r.Id == _s.BuildId;

    void RefreshRecent()
    {
        RenamingRecent = null;
        RecentBuilds.Clear();
        foreach (var r in _s.RecentBuilds.Where(r => r.Pinned).Concat(_s.RecentBuilds.Where(r => !r.Pinned)))
            RecentBuilds.Add(new RecentBuildVm(r, IsOpen(r)));
        foreach (var n in new[] { nameof(HasRecentBuilds), nameof(CanClearRecent), nameof(CurrentRecent), nameof(HasCurrentRecent),
                     nameof(PinnedRecent), nameof(OtherRecent), nameof(HasPinnedRecent), nameof(HasOtherRecent) })
            Raise(n);
    }
}
