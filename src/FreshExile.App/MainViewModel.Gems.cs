using System.Windows;
using System.Windows.Media;
using FreshExile.Core;

namespace FreshExile.App;

public static class GemPalette
{
    // socket colours, the same as Theme.xaml SocketR / SocketG / SocketB / SocketW
    static readonly Color Red = Color.FromRgb(0xD0, 0x50, 0x3F);
    static readonly Color Green = Color.FromRgb(0x5F, 0xA8, 0x45);
    static readonly Color Blue = Color.FromRgb(0x5B, 0x86, 0xD9);
    static readonly Color White = Color.FromRgb(0xD8, 0xD2, 0xC4);

    static readonly Dictionary<GemColour, (Brush Stroke, Brush Fill)> Cache = new();

    static Color Base(GemColour c) => c switch
    {
        GemColour.Red => Red,
        GemColour.Green => Green,
        GemColour.Blue => Blue,
        _ => White,
    };

    static (Brush Stroke, Brush Fill) Get(GemColour c)
    {
        if (Cache.TryGetValue(c, out var b)) return b;
        var col = Base(c);
        var stroke = new SolidColorBrush(col);
        // lit from the top-left like a gem orb
        var fill = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.3),
            Center = new Point(0.45, 0.45),
            RadiusX = 0.65,
            RadiusY = 0.65,
            GradientStops =
            {
                new GradientStop(Lerp(col, Colors.White, 0.55), 0),
                new GradientStop(col, 0.45),
                new GradientStop(Lerp(col, Colors.Black, 0.45), 1),
            },
        };
        stroke.Freeze();
        fill.Freeze();
        return Cache[c] = (stroke, fill);
    }

    static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Brush Stroke(GemColour c) => Get(c).Stroke;
    public static Brush Fill(GemColour c) => Get(c).Fill;
}

public sealed partial class MainViewModel
{
    GemDatabase? _gems;
    bool _gemsLoading;
    string? _gemStatus;

    public string? GemStatus { get => _gemStatus; private set { Set(ref _gemStatus, value); Raise(nameof(TreeInfo)); } }

    async Task EnsureGemsAsync(bool force = false)
    {
        if ((_gems != null && !force) || _gemsLoading) return;
        _gemsLoading = true;
        GemStatus = Loc.T("Загружаю данные гемов…");
        try
        {
            _gems = await GemRepository.LoadAsync(_http, force);
            GemStatus = null;
            Raise(nameof(TreeInfo));
            RefreshStage();
        }
        catch (Exception ex) { GemStatus = Loc.T($"Данные гемов не загрузились: {ex.Message}"); }
        finally { _gemsLoading = false; }
    }

    (string? Source, string? Tip, GemColour Colour, GemSource? From) DescribeGem(Gem gem)
    {
        if (_gems == null || Build == null) return (null, null, GemColour.None, null);
        var info = _gems.Find(gem);
        var src = _gems.WhereToGet(gem, Build.ClassName);

        string? text = null, tip = null;
        if (_gems.IsTransfigured(gem))
        {
            // the base gem's quest/vendor would be a lie here: this version is made in the Labyrinth
            text = Loc.T("Лаба · Divine Font");
            var baseName = info?.Name ?? Loc.T("базовый гем");
            tip = src == null ? Loc.T($"Улучшенная версия «{baseName}»: возьми базовый гем и преврати его в Divine Font после Izaro")
                : src.IsReward ? Loc.T($"Улучшенная версия «{baseName}»: возьми базовый гем (награда, акт {src.Act} · {src.Npc}) и преврати его в Divine Font после Izaro")
                : Loc.T($"Улучшенная версия «{baseName}»: возьми базовый гем (акт {src.Act} · {src.Npc}) и преврати его в Divine Font после Izaro");
        }
        else if (src != null)
        {
            text = src.IsReward ? Loc.T($"Акт {src.Act} · {src.Npc} · награда") : Loc.T($"Акт {src.Act} · {src.Npc}");
            tip = src.IsReward ? Loc.T($"Награда за квест «{src.Quest}»") : Loc.T($"Продаётся после квеста «{src.Quest}»");
        }
        if (info != null && _gameLevel > 0 && info.RequiredLevel > _gameLevel)
            text = (text == null ? "" : text + " · ") + Loc.T($"с {info.RequiredLevel} ур.");
        return (text, tip, info?.Colour ?? GemColour.None, src);
    }
}

/// <summary>One gem in the links view: the head of the link, or a "├"/"└" branch under it.</summary>
public sealed record LinkGemVm(string Name, System.Windows.Media.Brush Brush, bool IsHead, string Branch, string Level, bool KeepLevel)
{
    /// <summary>Only Cast when Damage Taken: its level is the point (lower triggers sooner, caps the supported gems).</summary>
    public string? LevelTip => KeepLevel ? Loc.T("Держи этот уровень: чем ниже CwDT, тем раньше срабатывает, и он ограничивает уровень гемов, которые вызывает") : null;

    /// <summary>A support the item in the slot gives, not a gem to socket: "от March of the Legion".</summary>
    public string? GrantedBy { get; init; }
    public bool IsGranted => GrantedBy != null;
    /// <summary>"  · от March of the Legion", empty for a gem you socket yourself (a StringFormat would print "· от" for those too).</summary>
    public string FromText => GrantedBy == null ? "" : Loc.T($"  · от {GrantedBy}");
    public string? NameTip => IsOff ? Loc.T("Выключен в PoB: в расчётах билда не участвует, но в связке стоит (часто его так держат нарочно)")
        : GrantedBy == null ? null : Loc.T($"Этот саппорт даёт предмет «{GrantedBy}», гем вставлять не нужно");

    /// <summary>Switched off in PoB: drawn dim with a note.</summary>
    public bool IsOff { get; init; }
    public string OffText => IsOff ? Loc.T("  · выкл. в PoB") : "";
}

/// <summary>A socket link as PoB draws it: the main gem, the rest hanging off it.</summary>
public sealed record LinkGroupVm(IReadOnlyList<LinkGemVm> Gems)
{
    /// <summary>"ШЛЕМ": the PoB slot the link sits in; empty when PoB does not say.</summary>
    public string SlotCaption { get; init; } = "";
    public bool HasSlot => SlotCaption.Length > 0;

    /// <summary>What the item in the slot changes for this link, in words ("ауры здесь — временный баф…").</summary>
    public string? Note { get; init; }
    public bool HasNote => Note != null;

    /// <summary>In the second weapon set: levelled there for later, drawn dashed and dim.</summary>
    public bool IsSwap { get; init; }

    /// <summary>The sockets the link needs, one per gem you socket, in the gems' colours ("4L" beside them).</summary>
    public IReadOnlyList<LinkSocketVm> Sockets => Gems.Where(g => !g.IsGranted)
        .Select((g, i) => new LinkSocketVm(g.Brush, i > 0)).ToList();
    public string LinkText => $"{Gems.Count(g => !g.IsGranted)}L";
}

/// <summary>One socket in a link's strip; Linked = joined to the one before it.</summary>
public sealed record LinkSocketVm(System.Windows.Media.Brush Brush, bool Linked);

public sealed partial class MainViewModel
{
    /// <summary>All links of the stage, bought or not — what the sockets should look like.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LinkGroupVm> LinkGroups { get; } = new();

    RelayCommand? _copyGem;

    /// <summary>A click on a gem in the links: its name to the clipboard, for the stash or a vendor's search box.</summary>
    public RelayCommand CopyGemCommand => _copyGem ??= new RelayCommand(p =>
    {
        if (p is not LinkGemVm g || g.IsGranted) return;
        Toast(TrySetClipboard(g.Name) ? Loc.T($"Скопировано: {g.Name}") : Loc.T("Буфер занят, ещё раз"));
    });
    public bool HasLinkGroups => LinkGroups.Count > 0;

    void RefreshLinks()
    {
        LinkGroups.Clear();
        // only a gear set named for this stage: another stage's item would put supports where there are none
        var gear = Build?.GearFor(Build.Stages[StageIndex]) is { Exact: true } g0 ? g0.Set : null;
        foreach (var g in Groups.Where(g => g.IsCard))
        {
            // the head is the first active gem (Steelskin, not Cast when Damage Taken listed before it)
            var head = g.Rows.FirstOrDefault(r => !r.IsSupport) ?? g.Rows[0];
            var rest = g.Rows.Where(r => r != head).ToList();
            var item = gear == null || g.Slot.Length == 0 ? null : gear.Slots.FirstOrDefault(s => s.Slot == g.Slot).Item;
            var granted = item?.GrantedSupports.ToList() ?? [];

            var gems = new List<LinkGemVm> { new(head.Name, head.OrbStroke, true, "", LinkLevel(head.Gem), KeepLevel(head.Gem)) };
            var off = g.Disabled;
            var total = rest.Count + off.Count + granted.Count;
            for (var i = 0; i < rest.Count; i++)
                gems.Add(new LinkGemVm(rest[i].Name, rest[i].OrbStroke, false, i == total - 1 ? "└" : "├", LinkLevel(rest[i].Gem), KeepLevel(rest[i].Gem)));
            for (var i = 0; i < off.Count; i++)
            {
                var (_, _, colour, _) = DescribeGem(off[i]);
                gems.Add(new LinkGemVm(off[i].Name, GemPalette.Stroke(colour), false, rest.Count + i == total - 1 ? "└" : "├", LinkLevel(off[i]), false)
                    { IsOff = true });
            }
            for (var i = 0; i < granted.Count; i++)
                gems.Add(new LinkGemVm(granted[i].Name, GrantedBrush, false, rest.Count + off.Count + i == total - 1 ? "└" : "├", $"lv {granted[i].Level}", false)
                    { GrantedBy = item!.Title });
            LinkGroups.Add(new LinkGroupVm(gems)
            {
                Note = item == null ? null : LinkNote(item, granted),
                SlotCaption = g.Slot.Length == 0 ? "" : GearItemVm.SlotName(g.Slot).ToUpperInvariant(),
                IsSwap = g.IsSwap,
            });
        }
        Raise(nameof(HasLinkGroups));
    }

    static readonly System.Windows.Media.Brush GrantedBrush = MakeGranted();
    static System.Windows.Media.Brush MakeGranted()
    {
        var b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD8, 0x80, 0x3A)); // unique-item orange: it comes from the item
        b.Freeze();
        return b;
    }

    /// <summary>One plain line about what the slot's item does to the link; null when it does nothing.</summary>
    static string? LinkNote(PobItem item, List<(string Name, int Level)> granted)
    {
        var parts = new List<string>();
        if (granted.Any(s => s.Name.Contains("Blessing", StringComparison.OrdinalIgnoreCase)))
            parts.Add(Loc.T("ауры здесь не резервируют: кастуешь, и они висят временным бафом за ману (More Duration его удлиняет)"));
        foreach (var bonus in item.SocketedBonuses) parts.Add(bonus);
        return parts.Count == 0 ? null : $"{item.Title}: {string.Join("; ", parts)}";
    }

    /// <summary>Level the build wants the gem at, quality only when PoB sets it: "lv 7", "lv 20 · 20%".</summary>
    static bool KeepLevel(Gem g) =>
        g.SkillId.StartsWith("SupportCastWhenDamageTaken", StringComparison.Ordinal) || g.Key == "cast when damage taken";

    static string LinkLevel(Gem g) => g.Quality > 0 ? $"lv {g.Level} · {g.Quality}%" : $"lv {g.Level}";
}
