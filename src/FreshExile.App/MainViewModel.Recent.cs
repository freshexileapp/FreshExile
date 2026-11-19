using System.Collections.ObjectModel;
using System.Globalization;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>A build in the recent list: what it is, where it came from, how far it went.</summary>
public sealed class RecentBuildVm(RecentBuild entry)
{
    public RecentBuild Entry { get; } = entry;
    public string Title => Entry.Title;

    /// <summary>"pobb.in/abc12 · этап 3 из 7 · 19.11".</summary>
    public string Details => string.Join("  ·  ", new[]
    {
        SourceText(Entry.Input),
        Entry.Stages > 1 ? Loc.T($"этап {Entry.StageIndex + 1} из {Entry.Stages}") : null,
        Entry.At.ToLocalTime().ToString("dd.MM", CultureInfo.InvariantCulture),
    }.Where(x => !string.IsNullOrEmpty(x)));

    /// <summary>A link without its scheme ("pobb.in/abc12"); a pasted PoB code is just "код PoB".</summary>
    public static string SourceText(string input)
    {
        var s = input.Trim();
        if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Loc.T("код PoB");
        s = s[(s.IndexOf("//", StringComparison.Ordinal) + 2)..];
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        return s.TrimEnd('/');
    }
}

public sealed partial class MainViewModel
{
    const int RecentMax = 8;

    /// <summary>Builds loaded before, the current one left out: one click brings one back, at the stage it was left on.</summary>
    public ObservableCollection<RecentBuildVm> RecentBuilds { get; } = new();
    public bool HasRecentBuilds => RecentBuilds.Count > 0;

    public RelayCommand OpenRecentCommand => _openRecent ??= new RelayCommand(p => { if (p is RecentBuildVm r) OpenRecent(r.Entry); }, _ => !IsBusy);
    RelayCommand? _openRecent;

    public RelayCommand RemoveRecentCommand => _removeRecent ??= new RelayCommand(p =>
    {
        if (p is not RecentBuildVm r) return;
        _s.RecentBuilds.Remove(r.Entry);
        RefreshRecent();
        Save();
    });
    RelayCommand? _removeRecent;

    /// <summary>
    /// The current build goes to the top of the list (or replaces its older self): the same build id, or the same link —
    /// a link whose PoB changed, or a chain of saved stages that grew, is still the same build.
    /// </summary>
    void RememberBuild()
    {
        if (Build == null || string.IsNullOrEmpty(_s.BuildXml) || _s.BuildId == null) return;
        var input = _s.BuildInput.Trim();
        var link = input.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        _s.RecentBuilds.RemoveAll(r => r.Id == _s.BuildId || (link && string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase)));
        _s.RecentBuilds.Insert(0, new RecentBuild
        {
            Id = _s.BuildId,
            Title = BuildLine + (Build.Level > 0 ? Loc.T($", ур. {Build.Level}") : ""),
            // a pasted code is the build itself: kept as XML below, not twice
            Input = link ? input : "",
            Xml = _s.BuildXml,
            StageIndex = _s.StageIndex,
            Stages = Build.Stages.Count,
            At = DateTime.UtcNow,
        });
        if (_s.RecentBuilds.Count > RecentMax) _s.RecentBuilds.RemoveRange(RecentMax, _s.RecentBuilds.Count - RecentMax);
        RefreshRecent();
    }

    /// <summary>Before another build takes over: the stage this one was left on.</summary>
    void KeepStageOfCurrent()
    {
        if (_s.RecentBuilds.FirstOrDefault(r => r.Id == _s.BuildId) is { } cur) cur.StageIndex = _s.StageIndex;
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
        _s.BuildId = build.Id;
        _s.BuildInput = entry.Input;
        _s.StageIndex = Math.Clamp(entry.StageIndex, 0, Math.Max(0, build.Stages.Count - 1));
        BuildInput = entry.Input;
        ApplyBuild(build);
        _tab = Tab.Gems;
        RaisePages();
        RememberBuild();
        Toast(Loc.T($"Открыт билд · {BuildLine}"));
        Save();
    }

    void RefreshRecent()
    {
        RecentBuilds.Clear();
        foreach (var r in _s.RecentBuilds.Where(r => r.Id != _s.BuildId)) RecentBuilds.Add(new RecentBuildVm(r));
        Raise(nameof(HasRecentBuilds));
    }
}
