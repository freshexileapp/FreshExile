using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>
/// Trade site search for a PoB item: a unique by name and base, anything else by its slot and mods
/// (stat ids from the trade site's own list, minimums a bit under PoB's rolls so something turns up).
/// The query goes into the page address (?q=), nothing is posted to the trade API.
/// </summary>
public sealed partial class TradeLink
{
    const string Site = "https://www.pathofexile.com";

    /// <summary>"#% increased Movement Speed" → stat id, per mod kind ("explicit", "implicit", "crafted"…).</summary>
    readonly Dictionary<string, Dictionary<string, string>> _stats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same for the item's own "(Local)" stats: armour and weapons roll those ("550% increased Evasion and Energy Shield").</summary>
    readonly Dictionary<string, Dictionary<string, string>> _local = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gem name as the game shows it → trade type (+ "alt_x" for a transfigured one, sold under its base).</summary>
    readonly Dictionary<string, (string Type, string? Disc)> _gems = new(StringComparer.OrdinalIgnoreCase);

    readonly HashSet<string> _types = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The trade site's type for a name: the name itself when the site knows it, else the longest known type inside it,
    /// whole words only ("Greater Engorged Eye" → "Engorged Eye"); the name as it is when nothing fits.
    /// </summary>
    public string TypeFor(string name)
    {
        if (_types.Count == 0 || _types.Contains(name)) return name;
        string? best = null;
        foreach (var t in _types)
        {
            if (best != null && t.Length <= best.Length) continue;
            var i = name.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var end = i + t.Length;
            if ((i == 0 || name[i - 1] == ' ') && (end == name.Length || name[end] == ' ')) best = t;
        }
        return best ?? name;
    }

    public string League { get; }

    /// <summary>
    /// The trade site's listing filter: "securable" = Instant Buyout, "available" = Instant Buyout and In Person,
    /// "onlineleague" / "online" = In Person, "any".
    /// </summary>
    public string Status { get; set; } = DefaultStatus;
    public const string DefaultStatus = "securable";

    /// <summary>Stat minimums as a share of PoB's roll, 0-100; 0 = stats without minimums, uniques by name only.</summary>
    public int Strictness { get; set; } = DefaultStrictness;
    public const int DefaultStrictness = 80;

    TradeLink(string league) => League = league;

    static string Dir => Path.Combine(StateStore.Dir, "trade");

    /// <summary>From data already in hand (tests, a cache read elsewhere).</summary>
    public static TradeLink FromData(string league, string? statsJson, string? itemsJson = null, string? staticJson = null)
    {
        var link = new TradeLink(league);
        if (statsJson != null) link.ReadStats(statsJson);
        if (itemsJson != null) link.ReadGems(itemsJson);
        if (staticJson != null) link.ReadStatic(staticJson);
        return link;
    }

    readonly Dictionary<string, string> _exchange = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _exchangeArt = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _exchangeGroup = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The trade's group of an exchange id ("imperial-enshrouding-crystal" → "EnshroudingCrystals"); null when unknown.</summary>
    public string? ExchangeGroup(string id) => _exchangeGroup.GetValueOrDefault(id);

    /// <summary>The picture of an exchange id ("divine", "eldritch-exalted-orb") on GGG's CDN; null when the site has none.</summary>
    public string? ExchangeImage(string id) => _exchangeArt.GetValueOrDefault(id);

    /// <summary>
    /// The bulk exchange's id of an item it trades ("Eldritch Exalted Orb" → "eldritch-exalted-orb"): currency, fragments,
    /// essences, cards, oils…; null for anything sold one by one.
    /// </summary>
    public string? ExchangeId(GameItem item) =>
        item.BaseType.Length > 0 && _exchange.TryGetValue(item.BaseType, out var b) ? b
        : item.Name.Length > 0 && _exchange.TryGetValue(item.Name, out var n) ? n
        : null;

    /// <summary>/api/trade/data/static: result[] = {id: "Currency", entries: [{id, text}]}.</summary>
    void ReadStatic(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var groups) || groups.ValueKind != JsonValueKind.Array) return;
        foreach (var g in groups.EnumerateArray())
        {
            if (!g.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
            var group = g.TryGetProperty("id", out var gid) ? gid.GetString() : null;
            var label = g.TryGetProperty("label", out var gl) && gl.GetString() is { Length: > 0 } lb ? lb : group ?? "";
            foreach (var e in entries.EnumerateArray())
            {
                // the exchange's name over the same base from the item list: the search then goes to the exchange
                if (e.TryGetProperty("text", out var ct) && ct.GetString() is { Length: > 0 } ctext)
                    _catalog[ctext] = new TradeEntry(ctext, ctext, null, label, TradeEntryKind.Exchange);
                if (group != null && e.TryGetProperty("id", out var eid) && eid.GetString() is { Length: > 0 } ei) _exchangeGroup.TryAdd(ei, group);
                if (e.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } i
                    && e.TryGetProperty("text", out var t) && t.GetString() is { Length: > 0 } text)
                    _exchange.TryAdd(text, i);
                // "/gen/image/…/CurrencyModValues.png": the site's own path, served by GGG's CDN
                if (e.TryGetProperty("id", out var iid) && iid.GetString() is { Length: > 0 } ii
                    && e.TryGetProperty("image", out var img) && img.GetString() is { Length: > 0 } path)
                    _exchangeArt.TryAdd(ii, path.StartsWith("https://", StringComparison.Ordinal) ? path : "https://web.poecdn.com" + (path.StartsWith('/') ? path : "/" + path));
            }
        }
    }

    /// <summary>League list and stat list from the trade site, cached for a day.</summary>
    public static async Task<TradeLink> LoadAsync(HttpClient http, string? leagueOverride, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        var leagues = await Cached(http, "leagues.json", "/api/trade/data/leagues", ct);
        var link = new TradeLink(string.IsNullOrWhiteSpace(leagueOverride) ? PickLeague(leagues) : leagueOverride.Trim());
        try { link.ReadStats(await Cached(http, "stats.json", "/api/trade/data/stats", ct)); }
        catch (Exception) { /* uniques still work by name without the stat list */ }
        try { link.ReadGems(await Cached(http, "items.json", "/api/trade/data/items", ct)); }
        catch (Exception) { /* plain gems still work by name */ }
        try { link.ReadStatic(await Cached(http, "static.json", "/api/trade/data/static", ct)); }
        catch (Exception) { /* currency is then searched like any item */ }
        return link;
    }

    static async Task<string> Cached(HttpClient http, string file, string api, CancellationToken ct)
    {
        var path = Path.Combine(Dir, file);
        if (File.Exists(path) && DateTime.Now - File.GetLastWriteTime(path) < TimeSpan.FromDays(1))
            return await File.ReadAllTextAsync(path, ct);
        try
        {
            var text = await http.GetStringAsync(Site + api, ct);
            await File.WriteAllTextAsync(path, text, ct);
            return text;
        }
        catch (Exception) when (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, ct); // offline: yesterday's list is still right
        }
    }

    /// <summary>The current softcore trade league: the first PC league that is not Standard, Hardcore, SSF or Ruthless.</summary>
    public static string PickLeague(string leaguesJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(leaguesJson);
            foreach (var l in doc.RootElement.GetProperty("result").EnumerateArray())
            {
                var id = l.TryGetProperty("id", out var v) ? v.GetString() ?? "" : "";
                var realm = l.TryGetProperty("realm", out var r) ? r.GetString() : "pc";
                if (realm != "pc" || id.Length == 0) continue;
                if (NotTradeRx().IsMatch(id)) continue;
                return id;
            }
        }
        catch (Exception) { }
        return "Standard";
    }

    void ReadGems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var group in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var label = group.TryGetProperty("label", out var gl) ? gl.GetString() ?? "" : "";
            var gems = group.TryGetProperty("id", out var gidc) && gidc.GetString() == "gem";
            // every base the site knows, for names that carry more than the base ("Greater Engorged Eye")
            foreach (var e in group.GetProperty("entries").EnumerateArray())
            {
                if (e.TryGetProperty("type", out var any) && any.GetString() is { Length: > 0 } ty)
                {
                    _types.Add(ty);
                    AddToCatalog(e, ty, label, gems);
                }
            }
            if (!group.TryGetProperty("id", out var gid) || gid.GetString() != "gem") continue;
            foreach (var e in group.GetProperty("entries").EnumerateArray())
            {
                var type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (type.Length == 0) continue;
                var text = e.TryGetProperty("text", out var x) ? x.GetString() : null;
                var disc = e.TryGetProperty("disc", out var d) ? d.GetString() : null;
                _gems.TryAdd(text ?? type, (type, disc));
            }
        }
    }

    readonly Dictionary<string, TradeEntry> _catalog = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every name the trade sells, for the search window.</summary>
    public IReadOnlyCollection<TradeEntry> Catalog => _catalog.Values;

    public IReadOnlyList<TradeEntry> FindNames(string? query, int max = 40) => TradeCatalog.Find(_catalog.Values, query, max);

    void AddToCatalog(JsonElement e, string type, string label, bool gem)
    {
        var text = e.TryGetProperty("text", out var t) ? t.GetString() : null;
        if (e.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
        {
            text ??= name + " " + type;
            _catalog.TryAdd(text, new TradeEntry(text, type, name, label, TradeEntryKind.Unique));
        }
        else if (gem) _catalog.TryAdd(text ?? type, new TradeEntry(text ?? type, type, null, label, TradeEntryKind.Gem));
        // "Map" with disc "map" and the like: the site's generic entries, the same name as a real base
        else if (!e.TryGetProperty("disc", out _)) _catalog.TryAdd(text ?? type, new TradeEntry(text ?? type, type, null, label, TradeEntryKind.Item));
    }

    void ReadStats(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var group in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var kind = group.TryGetProperty("id", out var gid) ? gid.GetString() ?? "" : "";
            if (!_stats.TryGetValue(kind, out var map)) _stats[kind] = map = new(StringComparer.OrdinalIgnoreCase);
            if (!_local.TryGetValue(kind, out var local)) _local[kind] = local = new(StringComparer.OrdinalIgnoreCase);
            foreach (var e in group.GetProperty("entries").EnumerateArray())
            {
                var text = e.GetProperty("text").GetString() ?? "";
                var id = e.GetProperty("id").GetString() ?? "";
                // "(Local)" entries share the text with the global one: kept apart, armour and weapons want the local one
                if (text.Contains(" (Local)", StringComparison.Ordinal)) local.TryAdd(Normalize(StripLocal(text)), id);
                else map.TryAdd(Normalize(text), id);
            }
        }
    }

    static string StripLocal(string s) => s.Replace(" (Local)", "", StringComparison.Ordinal);

    /// <summary>Numbers out, "#" in: "+45 to maximum Life" → "+# to maximum life".</summary>
    public static string Normalize(string text) => WsRx().Replace(NumRx().Replace(text, "#"), " ").Trim().ToLowerInvariant();

    static readonly Dictionary<string, string> Category = new()
    {
        ["Helmet"] = "armour.helmet", ["Body Armour"] = "armour.chest", ["Gloves"] = "armour.gloves", ["Boots"] = "armour.boots",
        ["Belt"] = "accessory.belt", ["Amulet"] = "accessory.amulet", ["Ring 1"] = "accessory.ring", ["Ring 2"] = "accessory.ring",
        ["Ring 3"] = "accessory.ring", ["Flask 1"] = "flask", ["Flask 2"] = "flask", ["Flask 3"] = "flask", ["Flask 4"] = "flask", ["Flask 5"] = "flask",
    };

    /// <param name="baseType">The real base when known (a magic item's name carries its affixes).</param>
    public string Url(string slot, PobItem item, string? baseType = null) =>
        $"{Site}/trade/search/{Uri.EscapeDataString(League)}?q={Uri.EscapeDataString(Query(slot, item, baseType).ToJsonString())}";

    public JsonObject Query(string slot, PobItem item, string? baseType = null)
    {
        var query = new JsonObject { ["status"] = new JsonObject { ["option"] = Status } };
        var localSlot = !slot.StartsWith("Ring", StringComparison.Ordinal) && slot is not ("Amulet" or "Belt")
                        && !slot.StartsWith("Flask", StringComparison.Ordinal);
        if (item.Rarity == ItemRarity.Unique)
        {
            query["name"] = item.Name;
            query["type"] = item.BaseType;
            // a unique is found by name; its rolled lines get minimums so a bad roll does not come first, and the line of its
            // version (The Light of Meaning's one bonus of many) is searched always: another version is another item
            var rolls = new JsonArray();
            foreach (var mod in item.Mods.Where(m => m.Variant || TimelessJewel.IsSeedLine(m.Text) || (m.Rolled && Strictness > 0)))
            {
                var id = Find(mod.Kind == ModKind.Implicit ? "implicit" : "explicit", mod.Text, localSlot);
                if (id == null) continue;
                var stat = new JsonObject { ["id"] = id };
                // a timeless jewel's seed: exactly this one, another seed is another jewel
                if (TimelessJewel.IsSeedLine(mod.Text) && GameItem.ValueOf(mod.Text) is double seed)
                {
                    stat["value"] = new JsonObject { ["min"] = seed, ["max"] = seed };
                    rolls.Add(stat);
                    continue;
                }
                if (Strictness > 0 && mod.Rolled && Minimum(mod.Text) is { } min) stat["value"] = new JsonObject { ["min"] = min };
                else if (!mod.Variant) continue;
                rolls.Add(stat);
            }
            if (rolls.Count > 0) query["stats"] = new JsonArray { new JsonObject { ["type"] = "and", ["filters"] = rolls } };
            return new JsonObject { ["query"] = query, ["sort"] = new JsonObject { ["price"] = "asc" } };
        }

        var filters = new JsonObject();
        // weapons, shields and flasks: the base is the point (a bow, a Bismuth Flask); armour and jewellery: the slot will do
        var flask = slot.StartsWith("Flask", StringComparison.Ordinal);
        if (flask && (baseType != null || item.Rarity != ItemRarity.Magic))
            query["type"] = baseType ?? item.BaseType;
        else if (Category.TryGetValue(slot, out var cat))
            filters["type_filters"] = new JsonObject { ["filters"] = new JsonObject { ["category"] = new JsonObject { ["option"] = cat } } };
        else if (baseType != null || item.Rarity != ItemRarity.Magic)
            query["type"] = baseType ?? item.BaseType;
        if (filters.Count > 0) query["filters"] = filters;

        var stats = new JsonArray();
        foreach (var mod in item.Mods)
        {
            var kind = mod.Kind switch
            {
                ModKind.Implicit => "implicit",
                ModKind.Crafted => "crafted",
                ModKind.Fractured => "fractured",
                ModKind.Enchant => "enchant",
                _ => "explicit",
            };
            // a crafted line can also be rolled: searching it as explicit finds more
            var id = Find(kind, mod.Text, localSlot) ?? (kind is "crafted" or "fractured" ? Find("explicit", mod.Text, localSlot) : null);
            if (id == null || kind == "implicit") continue; // the base already brings its implicit
            var stat = new JsonObject { ["id"] = id };
            var min = Minimum(mod.Text);
            if (min != null) stat["value"] = new JsonObject { ["min"] = min };
            stats.Add(stat);
            if (stats.Count >= 8) break;
        }
        if (stats.Count > 0)
            query["stats"] = new JsonArray { new JsonObject { ["type"] = "and", ["filters"] = stats } };
        return new JsonObject { ["query"] = query, ["sort"] = new JsonObject { ["price"] = "asc" } };
    }

    /// <summary>Stat id of a mod line for its kind ("explicit", "implicit", "crafted", "pseudo"…); null when the site has no such stat.</summary>
    public string? StatId(string kind, string text, bool preferLocal) => Find(kind, text, preferLocal);

    /// <summary>The search page for a query made elsewhere (the price check window).</summary>
    public string SearchUrl(JsonObject query) =>
        $"{Site}/trade/search/{Uri.EscapeDataString(League)}?q={Uri.EscapeDataString(query.ToJsonString())}";

    /// <summary>Trade type of a gem: a transfigured one is its base plus a discriminator.</summary>
    public JsonNode GemType(string name)
    {
        var (type, disc) = _gems.TryGetValue(name, out var g) ? g
            : _gems.TryGetValue(name + " Support", out var s) ? s
            : (name, (string?)null);
        return disc == null ? JsonValue.Create(type)! : new JsonObject { ["option"] = type, ["discriminator"] = disc };
    }

    string? Find(string kind, string text, bool preferLocal)
    {
        var key = Normalize(text);
        if (preferLocal && _local.TryGetValue(kind, out var local) && local.TryGetValue(key, out var lid)) return lid;
        if (_stats.TryGetValue(kind, out var map) && map.TryGetValue(key, out var id)) return id;
        return _local.TryGetValue(kind, out var any) && any.TryGetValue(key, out var aid) ? aid : null;
    }

    /// <summary>
    /// <see cref="Strictness"/>% of PoB's roll, so near-misses show up too. The trade site averages "Adds 5 to 10";
    /// any other line is judged by its first number ("97% … if you have at least 300 Dexterity" → 97).
    /// </summary>
    double? Minimum(string text)
    {
        if (Strictness <= 0) return null;
        var nums = NumRx().Matches(text).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        if (nums.Count == 0) return null;
        var v = nums.Count >= 2 && AddsRx().IsMatch(text) ? (nums[0] + nums[1]) / 2 : nums[0];
        if (v <= 0) return null;
        return Math.Floor(v * Strictness / 100.0);
    }

    [GeneratedRegex(@"\d+(?:\.\d+)?")] private static partial Regex NumRx();
    [GeneratedRegex(@"\s+")] private static partial Regex WsRx();
    [GeneratedRegex(@"\bAdds \d+(?:\.\d+)? to \d+", RegexOptions.IgnoreCase)] private static partial Regex AddsRx();
    [GeneratedRegex(@"Standard|Hardcore|\bHC\b|SSF|Solo|Ruthless", RegexOptions.IgnoreCase)] private static partial Regex NotTradeRx();
}
