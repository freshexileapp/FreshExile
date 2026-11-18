using FreshExile.Core;

namespace FreshExile.App;

public sealed partial class MainViewModel
{
    /// <summary>Gems of the previous stage that are gone now — take them out of the sockets.</summary>
    public string? SwapRemove { get; private set; }

    /// <summary>Gems that appear on this stage — put them in.</summary>
    public string? SwapAdd { get; private set; }

    public bool HasSwap => SwapRemove != null || SwapAdd != null;

    /// <summary>The user marked this stage's socket changes as made: the town block and the plate stop asking.</summary>
    public bool IsSwapDone
    {
        get => Build != null && _s.SwapDone.TryGetValue(Build.Id, out var done) && done.Contains(StageIndex);
        set
        {
            if (Build == null || value == IsSwapDone) return;
            if (!_s.SwapDone.TryGetValue(Build.Id, out var done)) _s.SwapDone[Build.Id] = done = new HashSet<int>();
            if (value) done.Add(StageIndex); else done.Remove(StageIndex);
            RaiseSwapDone();
            Save();
        }
    }

    /// <summary>The "ЗАМЕНА" lines in the town block: something to change in existing sockets, not done yet.</summary>
    public bool ShowMiniSwap => HasSwapRemove && !IsSwapDone;
    public bool ShowSwapLines => !IsSwapDone;

    public RelayCommand MarkSwapDoneCommand => _markSwapDone ??= new RelayCommand(() => IsSwapDone = true);
    RelayCommand? _markSwapDone;

    void RaiseSwapDone()
    {
        foreach (var n in new[] { nameof(IsSwapDone), nameof(ShowMiniSwap), nameof(ShowTownSwap), nameof(ShowSwapLines), nameof(ShowTownBlock) })
            Raise(n);
    }
    public bool HasSwapRemove => SwapRemove != null;
    public bool HasSwapAdd => SwapAdd != null;
    public string SwapCaption => Build == null || StageIndex == 0 ? "" : Loc.T($"СМЕНА СВЯЗОК ПОСЛЕ «{Build.Stages[StageIndex - 1].Title.ToUpperInvariant()}»");

    /// <summary>"Volley → Greater Volley", "Herald → убрать", "+ Grace".</summary>
    public System.Collections.ObjectModel.ObservableCollection<SwapLineVm> SwapLines { get; } = new();

    /// <summary>Only what changes in existing sockets — new gems are already in the town buy list.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SwapLineVm> MiniSwapLines { get; } = new();

    void ComputeSwap()
    {
        SwapRemove = SwapAdd = null;
        SwapLines.Clear();
        MiniSwapLines.Clear();
        if (Build != null && StageIndex > 0)
        {
            var lines = GemSwap.Diff(Build.Stages[StageIndex - 1], Build.Stages[StageIndex]);
            foreach (var l in lines)
            {
                // a transfigured gem is made in the Labyrinth, not swapped in town: it waits in the buy list as "Лаба"
                var lab = l.New != null && _gems?.IsTransfigured(l.New) == true;
                var vm = new SwapLineVm(l.Old?.Name, l.New?.Name) { FromLab = lab };
                SwapLines.Add(vm);
                if (l.Old != null && !lab) MiniSwapLines.Add(vm);
            }
            var removed = lines.Where(l => l.Old != null && !(l.New != null && _gems?.IsTransfigured(l.New) == true)).Select(l => l.Old!.Name).ToList();
            var added = lines.Where(l => l.New != null).Select(l => l.New!.Name).ToList();
            if (removed.Count > 0) SwapRemove = string.Join(", ", removed);
            if (added.Count > 0) SwapAdd = string.Join(", ", added);
        }
        foreach (var n in new[] { nameof(SwapRemove), nameof(SwapAdd), nameof(HasSwap), nameof(HasSwapRemove), nameof(HasSwapAdd), nameof(SwapCaption) })
            Raise(n);
        RaiseSwapDone();
    }

    /// <summary>Raised when the game moved the guide to a new stage that changes the gem setup.</summary>
    public event Action? SwapAnnounced;

    /// <summary>"Greater Volley — Акт 4 · Dialla" lines for the new gems still to buy.</summary>
    public List<string> AnnounceBuy { get; private set; } = new();
    public bool HasAnnounceBuy => AnnounceBuy.Count > 0;

    /// <summary>QA: "--demo-swap" on the command line shows the card for the current stage.</summary>
    public void DemoAnnounce() => AnnounceSwap();

    /// <summary>One line for the over-the-game plate: what waits in town. Details are in the panel.</summary>
    public string NoticeHint { get; private set; } = "";
    public bool HasNoticeHint => NoticeHint.Length > 0;

    /// <summary>Every stage switch made by the game gets a short plate, gem changes or not.</summary>
    void AnnounceSwap()
    {
        if (!Tracking) return;
        AnnounceBuy = !TrackGems ? [] : Groups.SelectMany(g => g.Rows)
            .Where(r => r.IsNew && !r.IsBought)
            .GroupBy(r => r.Gem.Key).Select(g => g.First())
            .Take(6)
            .Select(r => r.SourceText is { } s ? $"{r.Name} — {s}" : r.Name)
            .ToList();
        var parts = new List<string>();
        if (AnnounceBuy.Count > 0) parts.Add(Loc.T($"купить {AnnounceBuy.Count}"));
        if (ShowTownSwap) parts.Add(Loc.T("поменять связки"));
        if (HasLabText) parts.Add(Loc.T($"лаба: {LabText}"));
        NoticeHint = parts.Count == 0 ? "" : "· " + string.Join(", ", parts);
        foreach (var n in new[] { nameof(AnnounceBuy), nameof(HasAnnounceBuy), nameof(NoticeHint), nameof(HasNoticeHint) })
            Raise(n);
        SwapAnnounced?.Invoke();
    }

}

/// <summary>One socket change for display: Old → New, Old → убрать, + New.</summary>
public sealed record SwapLineVm(string? Old, string? New)
{
    public bool HasOld => Old != null;
    public string OldText => Old ?? "";
    public string Arrow => Old == null ? "+ " : "  →  ";
    /// <summary>The new gem is transfigured: done at the Divine Font, not in town.</summary>
    public bool FromLab { get; init; }
    public string NewText => New == null ? Loc.T("убрать") : FromLab ? Loc.T($"{New} · лаба") : New;
    public bool IsRemoval => New == null;
}
