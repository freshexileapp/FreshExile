using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>One mod line of a gear card; an anointment carries its oils.</summary>
public sealed record GearModVm(string Text, Brush Brush)
{
    public IReadOnlyList<OilVm> Oils { get; init; } = [];
    public bool HasOils => Oils.Count > 0;
}

/// <summary>
/// A Blight oil of an anointment: the game's own icon once downloaded (a drop in its colour until then, or offline),
/// its name on hover ("Crimson Oil").
/// </summary>
public sealed class OilVm(string id, string name, Brush fill, Brush edge) : Observable
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public Brush Fill { get; } = fill;
    public Brush Edge { get; } = edge;

    /// <summary>The oil's inventory art on GGG's CDN: the same address every league.</summary>
    public string IconUrl => $"https://web.poecdn.com/image/Art/2DItems/Currency/Oils/{Id}.png";

    ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); } }
    public bool HasIcon => _icon != null;

    static readonly Dictionary<string, (byte R, byte G, byte B)> Colours = new()
    {
        ["ClearOil"] = (0xD9, 0xD4, 0xC7), ["SepiaOil"] = (0x9A, 0x74, 0x4C), ["AmberOil"] = (0xE0, 0x9C, 0x2C),
        ["VerdantOil"] = (0x55, 0xA8, 0x3E), ["TealOil"] = (0x2F, 0xA8, 0x9C), ["AzureOil"] = (0x34, 0x7C, 0xDA),
        ["IndigoOil"] = (0x4A, 0x45, 0xC2), ["VioletOil"] = (0x94, 0x48, 0xD0), ["CrimsonOil"] = (0xC0, 0x24, 0x40),
        ["BlackOil"] = (0x22, 0x22, 0x26), ["OpalescentOil"] = (0xE2, 0xD8, 0xEE), ["SilverOil"] = (0xBF, 0xC5, 0xCF),
        ["GoldenOil"] = (0xE6, 0xB8, 0x3E), ["PrismaticOil"] = (0xE6, 0x70, 0xD4),
    };

    static Brush Frozen(byte r, byte g, byte b, byte a = 0xFF) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }

    /// <summary>"CrimsonOil" → "Crimson Oil" in its colour; a black oil gets a light rim so it shows on the dark card.</summary>
    public static OilVm Of(string id)
    {
        var name = id.EndsWith("Oil", StringComparison.Ordinal) ? id[..^3] + " Oil" : id;
        var (r, g, b) = Colours.TryGetValue(id, out var c) ? c : ((byte)0x99, (byte)0x99, (byte)0x99);
        return new OilVm(id, name, Frozen(r, g, b), id == "BlackOil" ? Frozen(0x80, 0x80, 0x88) : Frozen(0, 0, 0, 0x90));
    }
}

/// <summary>One influence under the item's base, in its colour; <paramref name="Sep"/> is the " · " before it.</summary>
public sealed record GearInfluenceVm(string Sep, string Name, Brush Brush);

/// <summary>A worn item drawn like the game's tooltip: rarity-coloured header, implicits, a rule, explicits.</summary>
public sealed partial class GearItemVm(string slot, PobItem item, RelayCommand trade, RelayCommand copy) : Observable
{
    // the game's own rarity colours, slightly lifted for the dark panel
    static readonly Brush Unique = Frozen(0xAF, 0x60, 0x25);
    static readonly Brush Rare = Frozen(0xFF, 0xFF, 0x77);
    static readonly Brush Magic = Frozen(0x88, 0x88, 0xFF);
    static readonly Brush Normal = Frozen(0xC8, 0xC8, 0xC8);
    static readonly Brush ModBlue = Frozen(0x88, 0x88, 0xFF);
    static readonly Brush CraftedBlue = Frozen(0xB4, 0xB4, 0xFF);
    static readonly Brush Fractured = Frozen(0xA2, 0x90, 0x60);
    static readonly Brush Enchant = Frozen(0xB8, 0xDA, 0xF2);
    static readonly Brush Corrupt = Frozen(0xD2, 0x00, 0x00);
    // each master in the colour of his symbol: the Exarch's ember, the Eater's ichor, the conquerors' sigils
    static readonly Brush ExarchBrush = Frozen(0xF2, 0x8A, 0x3A);
    static readonly Brush EaterBrush = Frozen(0x4E, 0xD2, 0xB4);
    static readonly Brush ShaperBrush = Frozen(0xD4, 0xE2, 0xFF);
    static readonly Brush ElderBrush = Frozen(0xB8, 0x8C, 0xF0);
    static readonly Brush CrusaderBrush = Frozen(0xF0, 0xD0, 0x5A);
    static readonly Brush HunterBrush = Frozen(0x8E, 0xD4, 0x4E);
    static readonly Brush RedeemerBrush = Frozen(0x6C, 0xC4, 0xF4);
    static readonly Brush WarlordBrush = Frozen(0xE2, 0x5A, 0x4A);

    /// <summary>The Russian client's names.</summary>
    internal static string InfluenceName(Influence x) => x switch
    {
        Influence.Shaper => Loc.T("Создатель"),
        Influence.Elder => Loc.T("Древний"),
        Influence.Crusader => Loc.T("Крестоносец"),
        Influence.Hunter => Loc.T("Охотник"),
        Influence.Redeemer => Loc.T("Избавительница"),
        Influence.Warlord => Loc.T("Вождь"),
        Influence.Exarch => Loc.T("Пылающий экзарх"),
        Influence.Eater => Loc.T("Пожиратель миров"),
        _ => x.ToString(),
    };

    internal static Brush BrushOf(Influence x) => x switch
    {
        Influence.Shaper => ShaperBrush,
        Influence.Elder => ElderBrush,
        Influence.Crusader => CrusaderBrush,
        Influence.Hunter => HunterBrush,
        Influence.Redeemer => RedeemerBrush,
        Influence.Warlord => WarlordBrush,
        Influence.Exarch => ExarchBrush,
        _ => EaterBrush,
    };

    static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    public string Slot { get; } = slot;
    public string SlotText { get; } = SlotName(slot);
    public PobItem Item { get; } = item;
    public string Title => Item.Title;
    public string BaseType => Item.BaseType;
    public bool ShowBase => Item.Rarity is ItemRarity.Rare or ItemRarity.Unique && Item.Name != Item.BaseType;
    public Brush RarityBrush => Item.Rarity switch
    {
        ItemRarity.Unique => Unique,
        ItemRarity.Rare => Rare,
        ItemRarity.Magic => Magic,
        _ => Normal,
    };

    /// <summary>The tree's oil recipe for a notable ("Allocates Toxic Strikes" → its three oils); null = no tree yet.</summary>
    public Func<string, string[]?>? Recipes { get; init; }

    IReadOnlyList<GearModVm>? _implicits;
    public IReadOnlyList<GearModVm> Implicits => _implicits ??= Item.Mods
        .Where(m => m.Kind is ModKind.Implicit or ModKind.Enchant)
        // a flask's "Used when Charges reach full", a cluster's "Adds 8 Passive Skills…", an anointment are enchantments
        // in game, drawn in its pale blue; an influence's line is painted in its master's colour, the rest as the game draws them
        .Select(m => new GearModVm(m.Text, m.Influence is { } x ? BrushOf(x)
            : m.Kind == ModKind.Enchant || Item.BaseType.Contains("Flask", StringComparison.Ordinal)
              || Item.BaseType.Contains("Cluster Jewel", StringComparison.Ordinal) ? Enchant : ModBlue)
        {
            Oils = AnointRx().Match(m.Text) is { Success: true } a && Recipes?.Invoke(a.Groups[1].Value.Trim()) is { } oils
                ? oils.Select(OilVm.Of).ToList() : [],
        }).ToList();

    [System.Text.RegularExpressions.GeneratedRegex(@"^Allocates (.+)$")]
    private static partial System.Text.RegularExpressions.Regex AnointRx();

    /// <summary>A cluster: its notables by name, taken or not by the stage's tree; their lines are painted by it.</summary>
    public IReadOnlyDictionary<string, bool> NotableTaken { get; init; } = new Dictionary<string, bool>();

    IReadOnlyList<GearModVm>? _explicits;
    public IReadOnlyList<GearModVm> Explicits => _explicits ??= Item.Mods
        .Where(m => m.Kind is ModKind.Explicit or ModKind.Crafted or ModKind.Fractured)
        .Select(m => NotableLine(m.Text) is { } taken
            // "1 Added Passive Skill is Unholy Grace": green when the tree takes it, grey and crossed when it does not
            ? new GearModVm(taken ? "✓ " + m.Text : "✗ " + m.Text, taken ? TakenBrush : SkippedBrush)
            // a fractured line keeps the game's own colour: that it cannot change matters more than whose it is
            : new GearModVm(m.Text, m.Kind switch
            {
                ModKind.Crafted => CraftedBlue,
                ModKind.Fractured => Fractured,
                _ => m.Influence is { } x ? BrushOf(x) : ModBlue,
            })).ToList();

    bool? NotableLine(string text)
    {
        const string Prefix = "1 Added Passive Skill is ";
        return NotableTaken.Count > 0 && text.StartsWith(Prefix, StringComparison.Ordinal)
               && NotableTaken.TryGetValue(text[Prefix.Length..].Trim(), out var taken) ? taken : null;
    }
    public bool HasImplicits => Implicits.Count > 0;
    /// <summary>"Пылающий экзарх · Пожиратель миров" under the base, each name in the colour its mods are painted.</summary>
    public IReadOnlyList<GearInfluenceVm> Influences { get; } = item.Influences
        .Select((x, i) => new GearInfluenceVm(i == 0 ? "" : " · ", InfluenceName(x), BrushOf(x))).ToList();
    public bool HasInfluences => Item.Influences.Count > 0;
    public bool HasExplicits => Explicits.Count > 0;
    public bool Corrupted => Item.Corrupted;
    public Brush CorruptBrush => Corrupt;
    /// <summary>A cluster jewel's notables, taken or not on this stage's tree, and its small passives.</summary>
    public IReadOnlyList<GearModVm> TreeLines { get; init; } = [];
    public bool HasTreeLines => TreeLines.Count > 0;
    public static readonly Brush TakenBrush = Frozen(0x8E, 0xD1, 0x7A);
    public static readonly Brush SkippedBrush = Frozen(0x80, 0x80, 0x86);

    /// <summary>A timeless jewel in a socket: Vilsol's calculator on it and that socket.</summary>
    public TimelessJewel? Timeless { get; init; }
    public int? Socket { get; init; }
    /// <summary>Passives in its radius the stage's tree does not take: greyed out on the site.</summary>
    public IReadOnlyList<int> NotTaken { get; init; } = [];
    public bool HasTimeless => Timeless != null;
    public string TimelessTip => Timeless == null ? "" : Loc.T($"{Timeless.Name}, сид {Timeless.Seed}, {Timeless.Conqueror}: что он меняет вокруг сокета (vilsol.github.io/timeless-jewels). Невзятые в билде пассивки там погашены.");
    RelayCommand? _timeless;
    public RelayCommand TimelessCommand => _timeless ??= new RelayCommand(() =>
    {
        if (Timeless != null) Process.Start(new ProcessStartInfo(Timeless.Url(Socket, NotTaken)) { UseShellExecute = true });
    });

    /// <summary>A jewel in the stage's tree (plain, abyss, timeless, cluster), listed apart from the worn items.</summary>
    public bool IsJewel => Slot is "Jewel" or "Cluster";

    // the compact list: a row with the art, the name and what matters of it; a click opens the full card
    bool _expanded;
    public bool IsExpanded { get => _expanded; set { if (Set(ref _expanded, value)) Raise(nameof(IsCollapsed)); } }
    public bool IsCollapsed => !_expanded;
    RelayCommand? _toggle;
    public RelayCommand ToggleCommand => _toggle ??= new RelayCommand(() => IsExpanded = !IsExpanded);

    /// <summary>A cluster: "Нотабли 3 из 3 · малые 2 из 3", in green when all its notables are taken.</summary>
    public string? ClusterSummary { get; init; }
    public Brush ClusterSummaryBrush { get; init; } = TakenBrush;

    /// <summary>
    /// What the folded row says under the name: a cluster's notables, a timeless jewel's seed, else the first
    /// explicit lines (a unique's name says the rest).
    /// </summary>
    public IReadOnlyList<GearModVm> Summary => ClusterSummary != null ? [new GearModVm(ClusterSummary, ClusterSummaryBrush)]
        : Timeless != null ? [new GearModVm($"{Timeless.Seed} · {Timeless.Conqueror}", ModBlue)]
        : Explicits.Take(2).ToList();
    public string MoreText => ClusterSummary != null || Timeless != null || Explicits.Count <= 2 ? "" : $"+{Explicits.Count - 2}";
    public bool HasMore => MoreText.Length > 0;

    public string SocketsText => Loc.T($"Сокеты  {Item.Sockets}");
    public bool HasSockets => !string.IsNullOrEmpty(Item.Sockets);

    public RelayCommand TradeCommand { get; } = trade;
    public RelayCommand CopyCommand { get; } = copy;

    string _copyButton = CopyIdle;
    public static readonly string CopyIdle = Loc.T("Копировать");
    public string CopyButton { get => _copyButton; set => Set(ref _copyButton, value); }

    System.Windows.Media.ImageSource? _icon;
    /// <summary>The game's inventory art, filled in once downloaded.</summary>
    public System.Windows.Media.ImageSource? Icon { get => _icon; set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); } }
    public bool HasIcon => _icon != null;

    string _tradeButton = TradeIdle;
    public static readonly string TradeIdle = Loc.T("Трейд");
    public string TradeButton { get => _tradeButton; set => Set(ref _tradeButton, value); }

    internal static string SlotName(string slot) => slot switch
    {
        "Weapon 1" => Loc.T("Оружие"),
        "Weapon 2" => Loc.T("Вторая рука"),
        "Weapon 1 Swap" => Loc.T("Свап · оружие"),
        "Weapon 2 Swap" => Loc.T("Свап · вторая рука"),
        "Helmet" => Loc.T("Шлем"),
        "Body Armour" => Loc.T("Нагрудник"),
        "Gloves" => Loc.T("Перчатки"),
        "Boots" => Loc.T("Сапоги"),
        "Amulet" => Loc.T("Амулет"),
        "Ring 1" => Loc.T("Кольцо 1"),
        "Ring 2" => Loc.T("Кольцо 2"),
        "Ring 3" => Loc.T("Кольцо 3"),
        "Belt" => Loc.T("Пояс"),
        _ when slot.StartsWith("Flask ", StringComparison.Ordinal) => Loc.T($"Фласка {slot[6..]}"),
        "Jewel" => Loc.T("Джевел"),
        "Cluster" => Loc.T("Кластер"),
        _ => slot,
    };
}

public sealed partial class MainViewModel
{
    public ObservableCollection<GearItemVm> Gear { get; } = new();
    public bool HasGear => Gear.Count > 0;
    public bool HasNoGear => Build != null && Gear.Count == 0;

    // the gear tab in two groups: what is worn, then the jewels in the tree (clusters among them)
    public IReadOnlyList<GearItemVm> GearWorn => Gear.Where(g => !g.IsJewel).ToList();
    public IReadOnlyList<GearItemVm> GearJewels => Gear.Where(g => g.IsJewel).ToList();
    public bool HasJewels => Gear.Any(g => g.IsJewel);
    public string JewelsCaption => Loc.T($"САМОЦВЕТЫ В ДЕРЕВЕ · {Gear.Count(g => g.IsJewel)}");

    /// <summary>One click opens every card (to read the whole set) or folds them back into rows.</summary>
    public string ExpandAllText => Gear.Count > 0 && Gear.All(g => g.IsExpanded) ? Loc.T("Свернуть всё") : Loc.T("Развернуть всё");
    RelayCommand? _expandAll;
    public RelayCommand ExpandAllCommand => _expandAll ??= new RelayCommand(() =>
    {
        var open = !Gear.All(g => g.IsExpanded);
        foreach (var g in Gear) g.IsExpanded = open;
        Raise(nameof(ExpandAllText));
    });

    string? _gearCaption;
    /// <summary>"НАБОР «EARLY MAPS»", or a warning when no set is named like the stage.</summary>
    public string? GearCaption { get => _gearCaption; private set => Set(ref _gearCaption, value); }

    string? _gearNote;
    public string? GearNote { get => _gearNote; private set { Set(ref _gearNote, value); Raise(nameof(HasGearNote)); } }
    public bool HasGearNote => !string.IsNullOrEmpty(GearNote);

    TradeLink? _trade;
    Task<TradeLink>? _tradeLoading;
    ItemIcons? _icons;
    Task<ItemIcons>? _iconsLoading;
    int _gearVersion;

    string TradeStatus => string.IsNullOrEmpty(State.TradeStatus) ? TradeLink.DefaultStatus : State.TradeStatus;

    void SetTradeStatus(string status, bool on)
    {
        if (!on || TradeStatus == status) return;
        State.TradeStatus = status;
        Save();
        foreach (var n in new[] { nameof(IsTradeInstant), nameof(IsTradeInstantOrPerson), nameof(IsTradeInPerson) }) Raise(n);
    }

    /// <summary>Settings: which listings the trade search opens with.</summary>
    public bool IsTradeInstant { get => TradeStatus == "securable"; set => SetTradeStatus("securable", value); }
    public bool IsTradeInstantOrPerson { get => TradeStatus == "available"; set => SetTradeStatus("available", value); }
    public bool IsTradeInPerson { get => TradeStatus == "online"; set => SetTradeStatus("online", value); }

    /// <summary>Settings slider: stat minimums as % of PoB's roll, 0 = none.</summary>
    public double TradeStrictness
    {
        get => State.TradeStrictness ?? TradeLink.DefaultStrictness;
        set
        {
            var v = (int)Math.Clamp(Math.Round(value / 5) * 5, 0, 100);
            if (State.TradeStrictness == v) return;
            State.TradeStrictness = v;
            Raise();
            Raise(nameof(TradeStrictnessText));
            Save();
        }
    }

    public string TradeStrictnessText => (int)TradeStrictness == 0
        ? Loc.T("без минимумов: уники по имени, у остального только сами статы")
        : Loc.T($"минимум {(int)TradeStrictness}% от значений предмета (из PoB или скопированного в игре)");

    /// <summary>Settings: league typed by hand; empty = the current softcore league.</summary>
    public string TradeLeagueInput
    {
        get => State.TradeLeague ?? "";
        set
        {
            var v = value.Trim();
            if ((State.TradeLeague ?? "") == v) return;
            State.TradeLeague = v.Length > 0 ? v : null;
            _trade = null;          // next search picks the new league up
            _tradeLoading = null;
            Raise();
            Save();
        }
    }

    RelayCommand? _tradeGem;

    /// <summary>
    /// A gem row's trade button: the price check window on this gem, cheapest first, with "Написать" on each offer —
    /// any level for a gem that levels up on the way, the build's level for an Enlighten or an awakened one.
    /// </summary>
    public RelayCommand TradeGemCommand => _tradeGem ??= new RelayCommand(p => { if (p is GemRowVm r) TradeGem(r); });

    /// <summary>A gem's text as the game would copy it, for the price check window.</summary>
    public event Action<string>? GemPriceRequested;

    void TradeGem(GemRowVm row)
    {
        // the database name carries "Support"; a transfigured gem keeps its own name
        var name = row.FromLab ? row.Name : _gems?.Find(row.Gem)?.Name ?? row.Name;
        GemPriceRequested?.Invoke(GemSearch.ItemText(name, row.IsSupport, GemSearch.MinLevel(name, row.Gem.Level)));
    }

    async Task<TradeLink> EnsureTradeAsync() => _trade ??= await (_tradeLoading ??= TradeLink.LoadAsync(_http, State.TradeLeague));

    /// <summary>Pictures for the cards on screen; a stage switch midway drops the stale ones.</summary>
    async Task LoadGearIconsAsync()
    {
        var version = ++_gearVersion;
        var cards = Gear.ToList();
        if (cards.Count == 0) return;
        try
        {
            var trade = await EnsureTradeAsync();
            _icons ??= await (_iconsLoading ??= ItemIcons.LoadAsync(_http, trade.League));
        }
        catch (Exception) { _tradeLoading = null; _iconsLoading = null; return; }

        // an anointment's oils: their own pictures, fetched once into the same cache
        foreach (var oil in cards.SelectMany(c => c.Implicits).SelectMany(m => m.Oils))
        {
            if (version != _gearVersion) return;
            if (await ItemIcons.FileFor(_http, oil.IconUrl) is { } path && LoadPicture(path) is { } pic) oil.Icon = pic;
        }

        foreach (var card in cards)
        {
            if (version != _gearVersion) return;
            var url = _icons.UrlFor(card.Item);
            if (url == null) continue;
            var file = await ItemIcons.FileFor(_http, url);
            if (file == null || version != _gearVersion) continue;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // file not kept locked
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                System.Windows.Media.ImageSource art = bmp;
                if (ItemIcons.IsFlaskStrip(url) && bmp.PixelWidth >= 3)
                {
                    var frame = new System.Windows.Media.Imaging.CroppedBitmap(bmp, new System.Windows.Int32Rect(0, 0, bmp.PixelWidth / 3, bmp.PixelHeight));
                    frame.Freeze();
                    art = frame;
                }
                card.Icon = art;
            }
            catch (Exception) { /* a broken download: the card stays without art */ }
        }
    }

    /// <summary>A downloaded picture, the file not kept locked; null when it does not decode.</summary>
    static ImageSource? LoadPicture(string path)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception) { return null; }
    }

    void RefreshGear()
    {
        Gear.Clear();
        GearCaption = null;
        GearNote = null;
        if (Build != null && Build.GearFor(Build.Stages[StageIndex]) is { } g)
        {
            var title = g.Set.Title.Length > 0 ? g.Set.Title : Loc.T("без названия");
            GearCaption = Loc.T($"НАБОР «{title.ToUpperInvariant()}»");
            if (!g.Exact) GearNote = Loc.T($"В PoB нет набора шмота под «{Build.Stages[StageIndex].Title}», показан «{title}»");
            foreach (var (slot, item) in g.Set.Slots)
            {
                GearItemVm? vm = null;
                vm = new GearItemVm(slot, item, new RelayCommand(() => OpenTrade(vm!)), new RelayCommand(() => _ = CopyItemAsync(vm!)))
                {
                    Recipes = Tree is { } tree ? tree.AnointRecipe : null,
                };
                Gear.Add(vm);
            }
        }
        if (Build != null) AddJewels(Build.Stages[StageIndex]);
        foreach (var n in new[] { nameof(HasGear), nameof(HasNoGear), nameof(GearWorn), nameof(GearJewels), nameof(HasJewels), nameof(JewelsCaption), nameof(ExpandAllText) })
            Raise(n);
        _ = LoadGearIconsAsync();
    }

    /// <summary>Jewels in the stage's tree after the worn items; a cluster lists what of it the tree takes.</summary>
    void AddJewels(Stage stage)
    {
        foreach (var (socket, item) in Build!.JewelsFor(stage))
        {
            var cluster = ClusterJewel.IsCluster(item);
            var lines = new List<GearModVm>();
            string? summary = null;
            var allTaken = true;
            IReadOnlyDictionary<string, bool> notables = new Dictionary<string, bool>();
            if (cluster && Tree != null && ClusterJewel.Allocation(item, socket, Tree, stage.Tree!.Nodes) is { } a)
            {
                // the notables are painted on their own "1 Added Passive Skill is …" lines; one not found there is listed here
                foreach (var n in a.Notables.Where(n => !item.Mods.Any(m => m.Text.EndsWith(" is " + n.Name, StringComparison.Ordinal))))
                    lines.Add(new GearModVm(n.Taken ? "✓ " + n.Name : Loc.T($"✗ {n.Name} — не взят"), n.Taken ? GearItemVm.TakenBrush : GearItemVm.SkippedBrush));
                notables = a.Notables.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.Any(n => n.Taken));
                if (a.SmallTotal > 0)
                    lines.Add(new GearModVm(Loc.T($"Малые пассивки: {a.SmallTaken} из {a.SmallTotal}"), a.SmallTaken > 0 ? GearItemVm.TakenBrush : GearItemVm.SkippedBrush));
                var taken = a.Notables.Count(n => n.Taken);
                allTaken = taken == a.Notables.Count;
                summary = Loc.T($"Нотабли {taken} из {a.Notables.Count}");
                if (a.SmallTotal > 0) summary += " · " + Loc.T($"малые {a.SmallTaken} из {a.SmallTotal}");
            }
            GearItemVm? vm = null;
            vm = new GearItemVm(cluster ? "Cluster" : "Jewel", item, new RelayCommand(() => OpenTrade(vm!)), new RelayCommand(() => _ = CopyItemAsync(vm!)))
            {
                TreeLines = lines,
                ClusterSummary = summary,
                NotableTaken = notables,
                ClusterSummaryBrush = allTaken ? GearItemVm.TakenBrush : GearItemVm.SkippedBrush,
                Timeless = TimelessJewel.From(item.Name, item.Mods.Select(m => m.Text)),
                Socket = socket,
                NotTaken = Tree != null && TimelessJewel.From(item.Name, item.Mods.Select(m => m.Text)) != null
                    ? TimelessJewel.NotTaken(Tree, socket, stage.Tree!.Nodes.ToHashSet()) : [],
            };
            Gear.Add(vm);
        }
    }

    /// <summary>Item text to the clipboard: paste into PoB or a chat.</summary>
    async Task CopyItemAsync(GearItemVm vm)
    {
        vm.CopyButton = TrySetClipboard(vm.Item.ToGameText()) ? Loc.T("Скопировано ✓") : Loc.T("Буфер занят");
        await Task.Delay(1800);
        vm.CopyButton = GearItemVm.CopyIdle;
    }

    /// <summary>The gear card's "Трейд": the price check window on this item, to pick the filters before the search.</summary>
    public event Action<string, IReadOnlyCollection<string>>? PriceCheckRequested;

    void OpenTrade(GearItemVm vm)
    {
        // a unique of several versions: the line of this one is ticked from the start
        var pick = vm.Item.Mods.Where(m => m.Variant).Select(m => m.Text).ToList();
        // a cluster: the notables this stage's tree takes; the smart search lets the others be any harmless ones
        pick.AddRange(vm.NotableTaken.Where(kv => kv.Value).Select(kv => "1 Added Passive Skill is " + kv.Key));
        PriceCheckRequested?.Invoke(vm.Item.ToGameText(ItemClassOf(vm.Slot, vm.Item)), pick);
    }

    /// <summary>The game's Item Class for a PoB slot: what the price check takes the trade category from.</summary>
    static string ItemClassOf(string slot, PobItem item) => slot switch
    {
        "Helmet" => "Helmets",
        "Body Armour" => "Body Armours",
        "Gloves" => "Gloves",
        "Boots" => "Boots",
        "Amulet" => "Amulets",
        "Belt" => "Belts",
        _ when slot.StartsWith("Ring", StringComparison.Ordinal) => "Rings",
        _ when slot.StartsWith("Flask", StringComparison.Ordinal) =>
            item.BaseType.Contains("Life Flask") ? "Life Flasks" : item.BaseType.Contains("Mana Flask") ? "Mana Flasks"
            : item.BaseType.Contains("Hybrid Flask") ? "Hybrid Flasks" : "Utility Flasks",
        "Jewel" => item.BaseType.Contains("Eye Jewel") ? "Abyss Jewels" : "Jewels",
        "Cluster" => "Cluster Jewels",
        _ => WeaponClass(item.BaseType),
    };

    /// <summary>A weapon's class by its base name; "" when unknown (searched by the base then).</summary>
    static string WeaponClass(string baseType)
    {
        (string Word, string Class)[] words =
        [
            ("Bow", "Bows"), ("Claw", "Claws"), ("Dagger", "Daggers"), ("Wand", "Wands"), ("Sceptre", "Sceptres"),
            ("Staff", "Staves"), ("Quiver", "Quivers"), ("Shield", "Shields"), ("Buckler", "Shields"), ("Kite", "Shields"),
        ];
        foreach (var (w, c) in words) if (baseType.Contains(w, StringComparison.Ordinal)) return c;
        return "";
    }
}
