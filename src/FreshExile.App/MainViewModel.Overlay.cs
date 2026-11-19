using FreshExile.Core;

namespace FreshExile.App;

public sealed partial class MainViewModel
{
    string? _alignStatus;
    DateTime _lastSync = DateTime.MinValue;
    bool _autoSynced;

    /// <summary>Allocated nodes changed (character sync, stage, tree data): the aligner template must be rebuilt.</summary>
    public event Action? AlignNodesChanged;

    public bool TreeOverlayEnabled
    {
        get => State.TreeOverlay;
        set
        {
            if (State.TreeOverlay == value) return;
            State.TreeOverlay = value;
            Raise();
            AlignStatus = value ? null : Loc.T("Наложение выключено");
            Save();
        }
    }

    public string? AlignStatus { get => _alignStatus; set => Set(ref _alignStatus, value); }

    /// <summary>The path is not found on the open tree: the status is a hint to act on, shown in the accent colour.</summary>
    bool _alignWarning;
    public bool AlignWarning { get => _alignWarning; set => Set(ref _alignWarning, value); }

    /// <summary>
    /// The path on the game's tree seen by screenshots and screen recordings. Off on every start: while it is on the overlay
    /// sees its own white lines and notices nodes just taken in game worse.
    /// </summary>
    public bool ClusterCardFull
    {
        get => State.ClusterCardFull;
        set
        {
            if (State.ClusterCardFull == value) return;
            State.ClusterCardFull = value;
            Raise();
            Save();
        }
    }

    bool _overlayInCapture;
    public bool TreeOverlayInCapture { get => _overlayInCapture; set => Set(ref _overlayInCapture, value); }

    /// <summary>What is gold on the in-game tree: the synced character (or the previous stage) plus what was seen taken.</summary>
    public IReadOnlyList<int>? AlignNodes =>
        _char != null ? Live(_char.Nodes).ToList()
        : Build != null ? Live(AssumedTaken()).ToList()
        : null;

    /// <summary>The stage's class start: a fresh character's tree is found around it (TreeAligner.BuildStructureTemplate).</summary>
    public int? ClassStartId => Tree != null && CurrentStage?.Tree is { } t ? Tree.ClassStart(t.ClassId)?.Id : null;

    /// <summary>Plan nodes seen allocated on the in-game tree since the last character sync.</summary>
    readonly HashSet<int> _liveTaken = new();

    /// <summary>Nodes seen refunded on the in-game tree since the last character sync (respec applied).</summary>
    readonly HashSet<int> _liveRefunded = new();

    /// <summary>What is allocated now: the known nodes, plus what the screen showed taken, minus what it showed refunded.</summary>
    IEnumerable<int> Live(IEnumerable<int> known) => known.Concat(_liveTaken).Where(id => !_liveRefunded.Contains(id)).Distinct();

    /// <summary>The overlay saw these plan nodes turn gold: drop them from the plan right away.</summary>
    /// <summary>A copy for the screen-reading thread: nodes taken on screen this session.</summary>
    public int[] LiveTakenSnapshot { get; private set; } = [];

    public void AddLiveTaken(IEnumerable<int> ids)
    {
        var added = false;
        foreach (var id in ids) { added |= _liveTaken.Add(id); added |= _liveRefunded.Remove(id); }
        if (!added) return;
        LiveTakenSnapshot = _liveTaken.ToArray();
        RecomputePlan();
        AlignNodesChanged?.Invoke();
    }

    /// <summary>The overlay saw these nodes lose their gold after a respec: the plan rebuilds without them.</summary>
    /// <summary>
    /// Seen taken, now without gold again: points spent in game and then cancelled (or refunded). Back to "to take".
    /// </summary>
    public void RemoveLiveTaken(IEnumerable<int> ids)
    {
        var removed = false;
        foreach (var id in ids) removed |= _liveTaken.Remove(id);
        if (!removed) return;
        LiveTakenSnapshot = _liveTaken.ToArray();
        RecomputePlan();
        AlignNodesChanged?.Invoke();
    }

    /// <summary>A copy for the screen-reading thread: nodes seen refunded this session.</summary>
    public int[] LiveRefundedSnapshot { get; private set; } = [];

    /// <summary>Seen refunded, now gold again: the respec was cancelled. Back to allocated (and red "to refund" again).</summary>
    public void RemoveLiveRefunded(IEnumerable<int> ids)
    {
        var removed = false;
        foreach (var id in ids) removed |= _liveRefunded.Remove(id);
        if (!removed) return;
        LiveRefundedSnapshot = _liveRefunded.ToArray();
        RecomputePlan();
        AlignNodesChanged?.Invoke();
    }

    public void AddLiveRefunded(IEnumerable<int> ids)
    {
        var added = false;
        foreach (var id in ids) { added |= _liveRefunded.Add(id); _liveTaken.Remove(id); }
        LiveTakenSnapshot = _liveTaken.ToArray();
        LiveRefundedSnapshot = _liveRefunded.ToArray();
        if (!added) return;
        RecomputePlan();
        AlignNodesChanged?.Invoke();
    }

    /// <summary>A fresh character sync is the truth again: what the screen saw is in it.</summary>
    void ClearLive()
    {
        _liveTaken.Clear();
        LiveTakenSnapshot = [];
        LiveRefundedSnapshot = [];
        _liveRefunded.Clear();
    }

    public string ScaleInfo => State.TreeScales.Count > 0
        ? Loc.T($"Запомнено уровней зума: {State.TreeScales.Count}. Новый зум подхватится сам за 1–2 с.")
        : Loc.T("Масштаб не откалиброван: открой дерево в игре.");

    public void SaveState()
    {
        Save();
        Raise(nameof(ScaleInfo));
    }

    public void Recalibrate()
    {
        State.TreeScale = null;
        State.TreeScales.Clear();
        State.TreeHeaderPatch = null;
        AlignStatus = Loc.T("калибрую…");
        SaveState();
        RecalibrateRequested?.Invoke();
    }

    public event Action? RecalibrateRequested;

    #region Step list under the in-game header

    /// <summary>Folded, the list shows the next few points; unfolded, all of them (up to a sane cap).</summary>
    const int ShortSteps = 10;
    const int MaxSteps = 40;

    public System.Collections.ObjectModel.ObservableCollection<PlanStepVm> PlanSteps { get; } = new();
    public bool HasPlanSteps => PlanSteps.Count > 0;
    public string? MoreSteps { get; private set; }
    public bool HasMoreSteps => MoreSteps != null;

    /// <summary>"показать все" / "свернуть" under the list.</summary>
    public bool StepsExpanded
    {
        get => State.TreeStepsExpanded;
        set { if (State.TreeStepsExpanded == value) return; State.TreeStepsExpanded = value; Save(); RebuildPlanSteps(); }
    }

    public RelayCommand ToggleStepsCommand => _toggleSteps ??= new RelayCommand(() => StepsExpanded = !StepsExpanded);
    RelayCommand? _toggleSteps;

    /// <summary>"what does each numbered node give" — a newcomer reads it next to the numbers on the tree.</summary>
    void RebuildPlanSteps()
    {
        PlanSteps.Clear();
        MoreSteps = null;
        HighlightNodeId = null;
        if (Plan != null && Tree != null)
        {
            var limit = StepsExpanded ? MaxSteps : ShortSteps;
            for (var i = 0; i < Plan.Order.Count && i < limit; i++)
            {
                var n = Tree.Nodes[Plan.Order[i]];
                var effect = n.Kind == FreshExile.Core.NodeKind.Mastery
                    ? MasteryText(n) ?? Loc.T("мастерство: эффект выбрать в игре")
                    : string.Join(", ", n.Stats.Take(n.Kind == FreshExile.Core.NodeKind.Normal ? 3 : 2));
                PlanSteps.Add(new PlanStepVm($"{i + 1}", n.Name, Shorten(effect, 90), n.IsKey, n.Id, i == 0));
            }
            if (Plan.Order.Count > limit) MoreSteps = StepsExpanded
                ? Loc.T($"и ещё {Plan.Order.Count - limit} — во вкладке «Дерево» · свернуть")
                : Loc.T($"ещё {Plan.Order.Count - limit}…");
            else if (StepsExpanded && Plan.Order.Count > ShortSteps) MoreSteps = Loc.T("свернуть");
        }
        Raise(nameof(HasPlanSteps));
        Raise(nameof(MoreSteps));
        Raise(nameof(HasMoreSteps));
    }

    int? _highlightNodeId;

    /// <summary>Node under the mouse in the step list: the tree rings it (or points to it when it is off screen).</summary>
    public int? HighlightNodeId { get => _highlightNodeId; set => Set(ref _highlightNodeId, value); }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd(' ', ',') + "…";

    #endregion

    public RelayCommand RecalibrateCommand => _recalibrate ??= new RelayCommand(Recalibrate);
    RelayCommand? _recalibrate;

    /// <summary>Points were probably spent in game: refresh the character (rate-limited).</summary>
    public void OnGameTreeClosed()
    {
        if (Account.Length > 0 && Character.Length > 0 && (DateTime.UtcNow - _lastSync).TotalSeconds > 60)
            _ = SyncCharacterAsync(quiet: true);
    }
}
