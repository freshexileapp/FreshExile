using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FreshExile.Core;

public sealed class TradeApiException(string message) : Exception(message);

/// <summary>The site asked to wait, or we hold back on our own so it never has to: the wait is in <see cref="Wait"/>.</summary>
public sealed class TradeRateLimitException(TimeSpan wait) : Exception(Text(wait))
{
    // a wait of minutes is the site's own penalty: it counts every search from this IP, the browser's too
    static string Text(TimeSpan w) => w.TotalMinutes >= 2
        ? Loc.T($"Трейд закрыл поиск на {Math.Ceiling(w.TotalMinutes)} мин: много поисков с этого IP (браузер тоже считается)")
        : Loc.T($"Лимит запросов трейда, подожди {Math.Max(1, (int)Math.Ceiling(w.TotalSeconds))} с");

    public TimeSpan Wait { get; } = wait;
}

/// <summary>One offer from the trade site.</summary>
public sealed record TradeListing(string Id, double Amount, string Currency, string Account, DateTime? Indexed, int ItemLevel, string? Sockets, bool Corrupted)
{
    /// <summary>The listed item's own art (its variant, not just its base): https://web.poecdn.com/…</summary>
    public string? Icon { get; init; }

    /// <summary>The fetch's "item" object as it came: read only when the player asks for the comparison.</summary>
    public string? ItemJson { get; init; }

    /// <summary>
    /// How many the seller has at this price: the stack's size on the item search (its price is per piece, the way the
    /// game and the bulk exchange read it), the stock on the exchange. 1 for an item that does not stack.
    /// </summary>
    public int Stock { get; init; } = 1;

    /// <summary>The item stacks (currency, fragments…): nothing to compare line by line.</summary>
    public bool Stackable { get; init; }

    /// <summary>
    /// The site's ready message to the seller ("@Name Hi, I would like to buy your…"): an "in person" offer has one,
    /// an instant buyout does not (that one is bought on the site, travelling to the seller's hideout).
    /// </summary>
    public string? Whisper { get; init; }

    /// <summary>A gem's level and quality, as the offer lists them.</summary>
    public int? GemLevel { get; init; }
    public int GemQuality { get; init; }

    /// <summary>An exchange offer that is not one for one: "4 for 1 divine" — what the seller asks, as a whole.</summary>
    public (double Get, double Pay)? Ratio { get; init; }

    OfferDetails? _offer;

    /// <summary>The offered item as far as the fetch tells it: quality, defences, mod lines. Read on first use; null when unreadable.</summary>
    public GameItem? Item => Offer().Item;

    /// <summary>Trade stat id → value on the offered item, from its "extended.hashes" (lines of one stat summed).</summary>
    public IReadOnlyDictionary<string, double> Stats => Offer().Stats;

    /// <summary>Each mod line of the offer with its trade stat id and tier.</summary>
    public IReadOnlyList<OfferLine> Lines => Offer().Lines;

    OfferDetails Offer() => _offer ??= ItemJson == null ? OfferDetails.Empty : TradeApi.ReadOffer(ItemJson, ItemLevel, Corrupted, Sockets);
}

/// <summary>A mod line of an offer: its text, kind, trade stat id and tier (from "extended"), when the site gave them.</summary>
public sealed record OfferLine(string Text, ModKind Kind, string? StatId, int? Tier)
{
    /// <summary>The affix's name ("Hale") and side, from extended.mods: how many tiers it has (<see cref="ModTiers"/>).</summary>
    public string? Name { get; init; }
    public bool? Prefix { get; init; }
}

public sealed record OfferDetails(GameItem? Item, IReadOnlyDictionary<string, double> Stats, IReadOnlyList<OfferLine> Lines)
{
    public static readonly OfferDetails Empty = new(null, new Dictionary<string, double>(), []);
}

public sealed record TradeSearchResult(string Id, IReadOnlyList<string> ItemIds, int Total);

/// <summary>A bulk exchange search: its id (for the site's own page), the offers, how many the site counts.</summary>
public sealed record TradeExchangeResult(string Id, IReadOnlyList<TradeListing> Offers, int Total);

/// <summary>
/// Rate limits as the trade site announces them in its headers ("X-Rate-Limit-Ip: 8:10:60,15:60:300" = 8 per 10 s, ban 60 s…).
/// We keep one request under every rule, wait out a short gap ourselves and refuse a long one rather than risk the ban.
/// No clock of its own — testable.
/// </summary>
public sealed class RateGate
{
    readonly List<(int Max, int Period)> _rules;
    readonly List<DateTime> _sent = new();
    DateTime _blockedUntil = DateTime.MinValue;

    /// <param name="rules">Until the site tells its own: deliberately modest guesses.</param>
    public RateGate(params (int Max, int Period)[] rules) => _rules = rules.ToList();

    public IReadOnlyList<(int Max, int Period)> Rules => _rules;

    /// <summary>How long to wait before the next request; zero = go.</summary>
    public TimeSpan WaitBefore(DateTime now)
    {
        if (now < _blockedUntil) return _blockedUntil - now;
        _sent.RemoveAll(t => (now - t).TotalSeconds > _rules.Select(r => r.Period).DefaultIfEmpty(0).Max());
        var wait = TimeSpan.Zero;
        foreach (var (max, period) in _rules)
        {
            // one in reserve: another tool of the same player (or the browser) shares the limit
            var allowed = Math.Max(1, max - 1);
            var inWindow = _sent.Where(t => (now - t).TotalSeconds < period).OrderBy(t => t).ToList();
            if (inWindow.Count < allowed) continue;
            var free = inWindow[inWindow.Count - allowed].AddSeconds(period) - now;
            if (free > wait) wait = free;
        }
        return wait;
    }

    public void Sent(DateTime now) => _sent.Add(now);

    /// <summary>Requests left before the tightest rule fills (the one kept in reserve not counted).</summary>
    public int Left(DateTime now)
    {
        if (now < _blockedUntil) return 0;
        return _rules.Select(r => Math.Max(1, r.Max - 1) - _sent.Count(t => (now - t).TotalSeconds < r.Period)).DefaultIfEmpty(1).Min();
    }

    /// <summary>Reads the policy and the state headers; a state already at the edge or a running ban blocks.</summary>
    public void Update(HttpResponseHeaders headers, DateTime now)
    {
        var rules = new List<(int, int)>();
        var state = new List<(int Count, int Period, int Ban)>();
        foreach (var scope in new[] { "Ip", "Account" })
        {
            if (headers.TryGetValues("X-Rate-Limit-" + scope, out var r)) rules.AddRange(ParseRules(string.Join(",", r)).Select(x => (x.A, x.B)));
            if (headers.TryGetValues($"X-Rate-Limit-{scope}-State", out var s)) state.AddRange(ParseRules(string.Join(",", s)));
        }
        Apply(rules, state, headers.RetryAfter?.Delta, now);
    }

    /// <summary>Header values already split: rules (max, period), states (count, period, ban seconds left).</summary>
    public void Apply(IReadOnlyList<(int Max, int Period)> rules, IReadOnlyList<(int Count, int Period, int Ban)> state, TimeSpan? retryAfter, DateTime now)
    {
        if (rules.Count > 0)
        {
            _rules.Clear();
            _rules.AddRange(rules.Distinct());
        }
        foreach (var (count, period, ban) in state)
        {
            if (ban > 0) Block(now.AddSeconds(ban));
            var rule = _rules.FirstOrDefault(r => r.Period == period);
            if (rule.Max > 0 && count >= rule.Max - 1) Block(now.AddSeconds(period));
        }
        if (retryAfter is { } ra) Block(now + ra);
    }

    void Block(DateTime until)
    {
        if (until > _blockedUntil) _blockedUntil = until;
    }

    public static List<(int A, int B, int C)> ParseRules(string value)
    {
        var list = new List<(int, int, int)>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = part.Split(':');
            if (p.Length == 3 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b) && int.TryParse(p[2], out var c))
                list.Add((a, b, c));
        }
        return list;
    }
}

/// <summary>
/// The trade site's own search: one POST for the ids, one GET for the first ten offers. Searches and fetches have separate
/// limits on the site, so separate gates here; nothing is sent without a press of the player.
/// </summary>
public sealed class TradeApi(HttpClient http)
{
    const string Site = "https://www.pathofexile.com";

    /// <summary>Gaps up to this long are waited out quietly; longer ones are refused with a message.</summary>
    static readonly TimeSpan MaxQuietWait = TimeSpan.FromSeconds(3);

    // as the site announced them in 2026 (search: 5/10 s, 15/min, 30/5 min, 600/6 h); its headers replace them on the first answer
    readonly RateGate _search = new((5, 10), (15, 60), (30, 300), (600, 21600));
    readonly RateGate _fetch = new((8, 4), (14, 12), (40, 60));

    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>One line per request into the app's log: what was sent, the answer and the site's limit state.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Searches left before the tightest rule fills: below two, an extra search (the "in person" retry) is not worth it.</summary>
    public int SearchesLeft => _search.Left(Now());

    /// <summary>How long until a search (and its fetch) would be let through: the cooldown bar shows at least this.</summary>
    public TimeSpan NextSearchWait
    {
        get
        {
            var now = Now();
            var a = _search.WaitBefore(now);
            var b = _fetch.WaitBefore(now);
            return a > b ? a : b;
        }
    }

    public async Task<TradeSearchResult> SearchAsync(string league, JsonObject query, CancellationToken ct = default)
    {
        var url = $"{Site}/api/trade/search/{Uri.EscapeDataString(league)}";
        using var body = new StringContent(query.ToJsonString(), Encoding.UTF8, "application/json");
        using var doc = await SendAsync(_search, () => new HttpRequestMessage(HttpMethod.Post, url) { Content = body }, ct);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
        var ids = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];
        var total = root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : ids.Count;
        // the filters as sent and how many matched: "nothing found" can then be told apart from a wrong filter.
        // The query holds stat ids and numbers only, no account or character
        if (total == 0) Log?.Invoke($"trade search found nothing: {query.ToJsonString()}");
        return new TradeSearchResult(id, ids, total);
    }

    /// <summary>
    /// The bulk exchange — where currency, fragments, essences, cards… are sold: who sells <paramref name="want"/> for any
    /// of <paramref name="have"/>, every offer in one answer with its price per piece and stock.
    /// </summary>
    public async Task<TradeExchangeResult> ExchangeAsync(string league, string want, IReadOnlyList<string> have, CancellationToken ct = default)
    {
        var url = $"{Site}/api/trade/exchange/{Uri.EscapeDataString(league)}";
        var query = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["status"] = new JsonObject { ["option"] = "online" },
                ["have"] = new JsonArray(have.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
                ["want"] = new JsonArray(JsonValue.Create(want)),
            },
            ["sort"] = new JsonObject { ["have"] = "asc" },
            ["engine"] = "new",
        };
        using var body = new StringContent(query.ToJsonString(), Encoding.UTF8, "application/json");
        using var doc = await SendAsync(_search, () => new HttpRequestMessage(HttpMethod.Post, url) { Content = body }, ct);
        return ReadExchange(doc.RootElement);
    }

    /// <summary>
    /// The exchange's answer: result = {id: {listing: {account, indexed, offers: [{exchange: {currency, amount},
    /// item: {amount, stock}}]}}} — pay "exchange.amount" of a currency for "item.amount" of the wanted thing.
    /// </summary>
    public static TradeExchangeResult ReadExchange(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
        var offers = new List<TradeListing>();
        if (root.TryGetProperty("result", out var res))
        {
            var entries = res.ValueKind switch
            {
                JsonValueKind.Object => res.EnumerateObject().Select(p => p.Value).ToList(),
                JsonValueKind.Array => res.EnumerateArray().ToList(),
                _ => [],
            };
            foreach (var e in entries)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("listing", out var l) || l.ValueKind != JsonValueKind.Object) continue;
                var account = l.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object && acc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                DateTime? indexed = l.TryGetProperty("indexed", out var ix) && ix.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(ix.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
                if (!l.TryGetProperty("offers", out var os) || os.ValueKind != JsonValueKind.Array) continue;
                foreach (var o in os.EnumerateArray())
                {
                    if (!o.TryGetProperty("exchange", out var ex) || !o.TryGetProperty("item", out var it)) continue;
                    var currency = ex.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
                    var pay = ex.TryGetProperty("amount", out var pa) && pa.ValueKind == JsonValueKind.Number ? pa.GetDouble() : 0;
                    var get = it.TryGetProperty("amount", out var ga) && ga.ValueKind == JsonValueKind.Number ? ga.GetDouble() : 0;
                    var stock = it.TryGetProperty("stock", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0;
                    if (pay <= 0 || get <= 0 || currency.Length == 0) continue;
                    var oid = e.TryGetProperty("id", out var eid) ? eid.GetString() ?? "" : "";
                    offers.Add(new TradeListing(oid, pay / get, currency, account, indexed, 0, null, false)
                    {
                        Stock = Math.Max(1, stock), Stackable = true, Ratio = get != 1 || pay != Math.Round(pay) ? (get, pay) : null,
                    });
                }
            }
        }
        var total = root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : offers.Count;
        return new TradeExchangeResult(id, offers, total);
    }

    /// <summary>Up to ten offers per call, as the site allows.</summary>
    public async Task<IReadOnlyList<TradeListing>> FetchAsync(string searchId, IEnumerable<string> itemIds, CancellationToken ct = default)
    {
        var ids = itemIds.Take(10).ToList();
        if (ids.Count == 0) return [];
        var url = $"{Site}/api/trade/fetch/{string.Join(",", ids)}?query={Uri.EscapeDataString(searchId)}";
        using var doc = await SendAsync(_fetch, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return ReadListings(doc.RootElement);
    }

    public static IReadOnlyList<TradeListing> ReadListings(JsonElement root)
    {
        var list = new List<TradeListing>();
        if (!root.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var id = e.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            if (!e.TryGetProperty("listing", out var l)) continue;
            double amount = 0;
            var currency = "";
            if (l.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                if (p.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number) amount = a.GetDouble();
                if (p.TryGetProperty("currency", out var c)) currency = c.GetString() ?? "";
            }
            var whisper = l.TryGetProperty("whisper", out var w) && w.ValueKind == JsonValueKind.String && w.GetString() is { Length: > 0 } wt ? wt : null;
            int? gemLevel = null;
            var gemQuality = 0;
            var account = l.TryGetProperty("account", out var acc) && acc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            DateTime? indexed = l.TryGetProperty("indexed", out var ix)
                && DateTime.TryParse(ix.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
            int ilvl = 0;
            string? sockets = null, icon = null;
            var corrupted = false;
            string? itemJson = null;
            int stack = 1;
            var stackable = false;
            if (e.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("stackSize", out var ss) && ss.ValueKind == JsonValueKind.Number)
                {
                    stackable = true;
                    stack = Math.Max(1, ss.GetInt32());
                }
                if (item.TryGetProperty("ilvl", out var lv) && lv.ValueKind == JsonValueKind.Number) ilvl = lv.GetInt32();
                if (item.TryGetProperty("icon", out var ic) && ic.GetString() is { } u && u.StartsWith("https://", StringComparison.Ordinal)) icon = u;
                corrupted = item.TryGetProperty("corrupted", out var co) && co.ValueKind == JsonValueKind.True;
                if (item.TryGetProperty("sockets", out var so) && so.ValueKind == JsonValueKind.Array) sockets = SocketText(so);
                // a gem: "Level" [["3 (Max)", 0]], "Quality" [["+20%", 1]]
                if (item.TryGetProperty("frameType", out var ft) && ft.ValueKind == JsonValueKind.Number && ft.GetInt32() == 4
                    && item.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
                    foreach (var prop in props.EnumerateArray())
                    {
                        var pn = prop.TryGetProperty("name", out var pnn) ? pnn.GetString() : null;
                        if (pn is not ("Level" or "Quality") || !prop.TryGetProperty("values", out var pv) || pv.ValueKind != JsonValueKind.Array
                            || pv.GetArrayLength() == 0 || pv[0].ValueKind != JsonValueKind.Array || pv[0].GetArrayLength() == 0) continue;
                        var digits = new string((pv[0][0].ValueKind == JsonValueKind.String ? pv[0][0].GetString() ?? "" : "").SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
                        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var pval)) continue;
                        if (pn == "Level") gemLevel = pval; else gemQuality = pval;
                    }
                // kept as it came: the comparison reads it on the player's press, the search never depends on it
                itemJson = item.GetRawText();
            }
            list.Add(new TradeListing(id, amount, currency, account, indexed, ilvl, sockets, corrupted)
                { Icon = icon, ItemJson = itemJson, Stock = stack, Stackable = stackable, Whisper = whisper, GemLevel = gemLevel, GemQuality = gemQuality });
        }
        return list;
    }

    /// <summary>The kept "item" object read for the comparison; (null, empty) when it is shaped other than we expect.</summary>
    public static OfferDetails ReadOffer(string json, int ilvl, bool corrupted, string? sockets)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var (item, stats, lines) = ReadItem(doc.RootElement, ilvl, corrupted, sockets);
            return new OfferDetails(item, stats, lines);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or IndexOutOfRangeException or KeyNotFoundException)
        {
            return OfferDetails.Empty;
        }
    }

    static readonly (string Key, ModKind Kind)[] ModArrays =
    [
        ("implicitMods", ModKind.Implicit), ("explicitMods", ModKind.Explicit), ("craftedMods", ModKind.Crafted),
        ("fracturedMods", ModKind.Fractured), ("enchantMods", ModKind.Enchant),
    ];

    /// <summary>
    /// The fetched item's lines and properties as a <see cref="GameItem"/> (so its totals are counted the way ours are), and its
    /// lines with their stat ids and tiers: "extended.hashes" goes line by line, in the order of the mod arrays.
    /// </summary>
    static (GameItem, Dictionary<string, double>, List<OfferLine>) ReadItem(JsonElement item, int ilvl, bool corrupted, string? sockets)
    {
        var mods = new List<GameMod>();
        // the lines of each mod array as they come, those the site described in place already resolved
        var lines = new Dictionary<string, List<(string Text, OfferLine? Inline)>>();
        foreach (var (key, kind) in ModArrays)
        {
            var texts = new List<(string, OfferLine?)>();
            if (item.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var t in arr.EnumerateArray())
                {
                    var inline = InlineLine(t, kind);
                    // the index must stay in step with extended.hashes: an entry we cannot read keeps its place, empty
                    var s = inline?.Text ?? TextOf(t) ?? "";
                    texts.Add((s, inline));
                    if (s.Length > 0) mods.Add(new GameMod(s, inline?.Kind ?? kind));
                }
            lines[key[..^4]] = texts; // "explicitMods" → "explicit", the key of extended.hashes
        }

        int quality = 0;
        int? armour = null, evasion = null, es = null, ward = null;
        if (item.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
            foreach (var p in props.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var name = p.TryGetProperty("name", out var n) ? TextOf(n) ?? "" : "";
                if (!p.TryGetProperty("values", out var vals) || vals.ValueKind != JsonValueKind.Array || vals.GetArrayLength() == 0) continue;
                var first = vals[0];
                var text = first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0 ? TextOf(first[0]) ?? "" : "";
                if (GameItem.ValueOf(text) is not double v) continue;
                switch (name)
                {
                    case "Quality": quality = (int)v; break;
                    case "Armour": armour = (int)v; break;
                    case "Evasion Rating": evasion = (int)v; break;
                    case "Energy Shield": es = (int)v; break;
                    case "Ward": ward = (int)v; break;
                }
            }

        // extended.hashes.<kind>[j] = [stat id, [indexes into extended.mods.<kind>]] for the j-th line of that kind;
        // the mods it points to carry the tier ("S1", "P2")
        var stats = new Dictionary<string, double>();
        var offerLines = new List<OfferLine>();
        JsonElement ext = default;
        var hasExt = item.TryGetProperty("extended", out ext) && ext.ValueKind == JsonValueKind.Object;
        foreach (var (key, kind) in ModArrays)
        {
            var group = key[..^4];
            var entries = lines[group];
            // extended.hashes lists only the lines given as plain text (a fractured line described in place among the
            // explicit ones is not in hashes.explicit): its index counts those alone
            var texts = entries.Where(e => e.Inline == null).Select(e => e.Text).ToList();
            foreach (var (text, inline) in entries)
                if (inline != null && text.Length > 0)
                {
                    offerLines.Add(inline);
                    if (inline.StatId is { } sid && GameItem.ValueOf(text) is double iv) stats[sid] = stats.GetValueOrDefault(sid) + iv;
                }
            JsonElement hashes = default, modInfo = default;
            var hasHashes = hasExt && ext.TryGetProperty("hashes", out var hs) && hs.ValueKind == JsonValueKind.Object
                            && hs.TryGetProperty(group, out hashes) && hashes.ValueKind == JsonValueKind.Array;
            var hasMods = hasExt && ext.TryGetProperty("mods", out var ms) && ms.ValueKind == JsonValueKind.Object
                          && ms.TryGetProperty(group, out modInfo) && modInfo.ValueKind == JsonValueKind.Array;
            for (var j = 0; j < texts.Count; j++)
            {
                if (texts[j].Length == 0) continue;
                string? stat = null, name = null;
                int? tier = null;
                bool? prefix = null;
                if (hasHashes && j < hashes.GetArrayLength() && hashes[j] is { ValueKind: JsonValueKind.Array } h && h.GetArrayLength() > 0)
                {
                    stat = TextOf(h[0]);
                    if (hasMods && h.GetArrayLength() > 1 && h[1].ValueKind == JsonValueKind.Array)
                        foreach (var ix in h[1].EnumerateArray())
                        {
                            if (ix.ValueKind != JsonValueKind.Number || ix.GetInt32() is var k && (k < 0 || k >= modInfo.GetArrayLength())) continue;
                            var mi = modInfo[ix.GetInt32()];
                            if (mi.ValueKind != JsonValueKind.Object) continue;
                            var ts = mi.TryGetProperty("tier", out var t) ? TextOf(t) : null;
                            // the best tier of the affixes making this line names it
                            if (TierOf(ts) is int tv && (tier == null || tv < tier))
                            {
                                tier = tv;
                                name = mi.TryGetProperty("name", out var nm) ? TextOf(nm) : null;
                                prefix = ts![0] is 'P' or 'p' ? true : ts[0] is 'S' or 's' ? false : null;
                            }
                        }
                }
                offerLines.Add(new OfferLine(texts[j], kind, stat, tier) { Name = name, Prefix = prefix });
                if (stat != null && GameItem.ValueOf(texts[j]) is double v) stats[stat] = stats.GetValueOrDefault(stat) + v;
            }
        }

        var offered = new GameItem
        {
            BaseType = item.TryGetProperty("baseType", out var bt) ? TextOf(bt) ?? "" : "",
            ItemLevel = ilvl, Quality = quality, Armour = armour, Evasion = evasion, EnergyShield = es, Ward = ward,
            Corrupted = corrupted, Sockets = sockets, Mods = mods,
        };
        return (offered, stats, offerLines);
    }

    /// <summary>
    /// A mod line the site describes in place — {"description", "hash": "stat.fractured.stat_…", "domain", "flags",
    /// "mods": [{"name", "tier"}]} — as an offer line: its own stat id, its kind by domain (a fractured line comes among the
    /// explicit ones), its best tier. Null for a plain string (the older answer, resolved through extended.hashes).
    /// </summary>
    static OfferLine? InlineLine(JsonElement e, ModKind kind)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("hash", out var h) || h.GetString() is not { Length: > 0 } hash) return null;
        var text = TextOf(e) ?? "";
        var stat = hash.StartsWith("stat.", StringComparison.Ordinal) ? hash[5..] : hash;
        var domain = e.TryGetProperty("domain", out var d) ? d.GetString() : null;
        var fractured = domain == "fractured" || (e.TryGetProperty("flags", out var f) && f.ValueKind == JsonValueKind.Object
                                                   && f.TryGetProperty("fractured", out var fr) && fr.ValueKind == JsonValueKind.True);
        kind = fractured ? ModKind.Fractured
            : domain switch { "crafted" => ModKind.Crafted, "enchant" => ModKind.Enchant, "implicit" => ModKind.Implicit, _ => kind };
        int? tier = null;
        string? name = null;
        bool? prefix = null;
        if (e.TryGetProperty("mods", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                var ts = m.TryGetProperty("tier", out var t) ? TextOf(t) : null;
                if (TierOf(ts) is not int tv || (tier != null && tv >= tier)) continue;
                tier = tv;
                name = m.TryGetProperty("name", out var nm) ? TextOf(nm) : null;
                prefix = ts![0] is 'P' or 'p' ? true : ts[0] is 'S' or 's' ? false : null;
            }
        return new OfferLine(text, kind, stat, tier) { Name = name, Prefix = prefix };
    }

    /// <summary>"S1" / "P2" / "R3" → 1 / 2 / 3; null for anything without a number.</summary>
    static int? TierOf(string? s) =>
        s is { Length: > 1 } && int.TryParse(s.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null;

    /// <summary>A string as it is; an object by its "text" / "description"; anything else null. Never throws.</summary>
    static string? TextOf(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Object => new[] { "text", "description", "mod" }
            .Select(k => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
            .FirstOrDefault(s => s != null),
        _ => null,
    };

    /// <summary>[{group:0, sColour:"R"}, {group:0,…}, {group:1,…}] → "R-R G".</summary>
    static string SocketText(JsonElement sockets)
    {
        var sb = new StringBuilder();
        var last = -1;
        foreach (var s in sockets.EnumerateArray())
        {
            var g = s.TryGetProperty("group", out var gr) && gr.ValueKind == JsonValueKind.Number ? gr.GetInt32() : 0;
            var c = s.TryGetProperty("sColour", out var col) ? col.GetString() ?? "?" : "?";
            if (last >= 0) sb.Append(g == last ? '-' : ' ');
            sb.Append(c);
            last = g;
        }
        return sb.ToString();
    }

    async Task<JsonDocument> SendAsync(RateGate gate, Func<HttpRequestMessage> make, CancellationToken ct)
    {
        var wait = gate.WaitBefore(Now());
        if (wait > MaxQuietWait) throw new TradeRateLimitException(wait);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

        gate.Sent(Now());
        using var req = make();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await http.SendAsync(req, ct);
        gate.Update(resp.Headers, Now());
        Log?.Invoke($"trade {(gate == _search ? "search" : "fetch")}: {(int)resp.StatusCode}"
                    + (resp.Headers.TryGetValues("X-Rate-Limit-Ip", out var rl) ? $" rules {string.Join(",", rl)}" : "")
                    + (resp.Headers.TryGetValues("X-Rate-Limit-Ip-State", out var st) ? $" state {string.Join(",", st)}" : "")
                    + (resp.Headers.TryGetValues("X-Rate-Limit-Account-State", out var ast) ? $" account {string.Join(",", ast)}" : "")
                    + (resp.Headers.RetryAfter?.Delta is { } ra ? $" retry-after {ra.TotalSeconds}s" : ""));

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new TradeRateLimitException(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
        var json = await resp.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new TradeApiException(Loc.T($"Трейд ответил {(int)resp.StatusCode}")); }
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err))
        {
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
            doc.Dispose();
            throw new TradeApiException(msg is { Length: > 0 } ? Loc.T($"Трейд: {msg}") : Loc.T($"Трейд ответил {(int)resp.StatusCode}"));
        }
        if (!resp.IsSuccessStatusCode)
        {
            doc.Dispose();
            throw new TradeApiException(Loc.T($"Трейд ответил {(int)resp.StatusCode}"));
        }
        return doc;
    }
}

/// <summary>
/// Chaos per divine from poe.ninja, for "≈ 148c" next to divine prices. The in-game currency exchange first (what the game's
/// own Market Ratio shows, by volume); the stash-listing overview only as a fallback: a few dozen listings, hours old.
/// Optional: null when poe.ninja is down.
/// </summary>
public static class NinjaRates
{
    const string Exchange = "https://poe.ninja/poe1/api/economy/exchange/current/overview";
    const string Stash = "https://poe.ninja/poe1/api/economy/stash/current/currency/overview";

    public static async Task<double?> DivineChaosAsync(HttpClient http, string league, CancellationToken ct = default)
    {
        var q = $"?league={Uri.EscapeDataString(league)}&type=Currency";
        try
        {
            if (ReadExchangeDivine(await http.GetStringAsync(Exchange + q, ct)) is double d) return d;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        try { return ReadDivine(await http.GetStringAsync(Stash + q, ct)); }
        catch (Exception) { return null; }
    }

    /// <summary>Exchange overview: lines[id = "divine"].primaryValue is chaos per divine.</summary>
    public static double? ReadExchangeDivine(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("lines", out var lines)) return null;
        foreach (var l in lines.EnumerateArray())
            if (l.TryGetProperty("id", out var id) && id.GetString() == "divine"
                && l.TryGetProperty("primaryValue", out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() > 1)
                return v.GetDouble();
        return null;
    }

    /// <summary>Stash overview: the Divine Orb line's chaosEquivalent.</summary>
    public static double? ReadDivine(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("lines", out var lines)) return null;
        foreach (var l in lines.EnumerateArray())
            if (l.TryGetProperty("currencyTypeName", out var n) && n.GetString() == "Divine Orb"
                && l.TryGetProperty("chaosEquivalent", out var c) && c.ValueKind == JsonValueKind.Number)
                return c.GetDouble();
        return null;
    }
}

/// <summary>A unique's price on poe.ninja: an answer without spending a single trade request.</summary>
public sealed record NinjaPrice(double Chaos, double Divine, int Links)
{
    /// <summary>The rate of the in-game currency exchange (poe.ninja's exchange overview), not a unique's listed price.</summary>
    public bool Market { get; init; }
}

/// <summary>
/// The in-game currency exchange's rates as poe.ninja collects them, by the trade site's exchange ids
/// ("eldritch-exalted-orb" → 3.65 chaos): the real market for currency and the like, which the trade site does not show —
/// its bulk sellers are a handful and often ask far more. One fetch per list and league, kept an hour.
/// </summary>
public sealed class NinjaExchange(HttpClient http)
{
    const string Url = "https://poe.ninja/poe1/api/economy/exchange/current/overview";
    static readonly string[] Types =
        ["Currency", "Fragment", "Oil", "Essence", "DivinationCard", "DeliriumOrb", "Fossil", "Resonator", "Omen", "Tattoo", "Runegraft", "Artifact", "AllflameEmber"];
    /// <summary>Exchange goods poe.ninja prices only in its item lists (its exchange overview has none of them): the Maven's invitations.</summary>
    const string StashUrl = "https://poe.ninja/poe1/api/economy/stash/current/item/overview";
    static readonly string[] StashTypes = ["Invitation"];

    readonly Dictionary<string, (DateTime At, Dictionary<string, double> Chaos, HashSet<string> Types)> _cache = new();

    /// <param name="tradeGroup">The item's group on the trade ("EnshroudingCrystals"): a league's own kind poe.ninja lists
    /// under its own type, asked for when the usual lists do not have the item.</param>
    public async Task<double?> ChaosAsync(string exchangeId, string league, string? tradeGroup = null, CancellationToken ct = default)
    {
        if (!_cache.TryGetValue(league, out var c) || DateTime.UtcNow - c.At > TimeSpan.FromHours(1))
        {
            var all = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var json in await Task.WhenAll(Types.Select(t => GetAsync(t, league, ct))))
                if (json != null) Read(json, all);
            foreach (var json in await Task.WhenAll(StashTypes.Select(t => GetAsync(t, league, ct, StashUrl))))
                if (json != null) ReadStash(json, all);
            if (all.Count == 0) return null;
            _cache[league] = c = (DateTime.UtcNow, all, new HashSet<string>(Types, StringComparer.OrdinalIgnoreCase));
        }
        if (c.Chaos.TryGetValue(exchangeId, out var v)) return v;
        if (NinjaType(tradeGroup) is { } extra && c.Types.Add(extra) && await GetAsync(extra, league, ct) is { } more)
        {
            Read(more, c.Chaos);
            if (c.Chaos.TryGetValue(exchangeId, out v)) return v;
        }
        return null;
    }

    async Task<string?> GetAsync(string type, string league, CancellationToken ct, string url = Url)
    {
        try { return await http.GetStringAsync($"{url}?league={Uri.EscapeDataString(league)}&type={type}", ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>The trade's group → poe.ninja's type: "EnshroudingCrystals" → "EnshroudingCrystal", "Cards" → "DivinationCard".</summary>
    public static string? NinjaType(string? tradeGroup) => tradeGroup switch
    {
        null or "" => null,
        "Cards" => "DivinationCard",
        _ when tradeGroup.EndsWith('s') => tradeGroup[..^1],
        _ => tradeGroup,
    };

    /// <summary>lines[] = {id: "eldritch-exalted-orb", primaryValue: 3.65} — the price in chaos.</summary>
    public static void Read(string json, Dictionary<string, double> into)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return;
            foreach (var l in lines.EnumerateArray())
                if (l.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } i
                    && l.TryGetProperty("primaryValue", out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() > 0)
                    into.TryAdd(i, v.GetDouble());
        }
        catch (JsonException) { /* a list that did not come whole: the others still price */ }
    }

    /// <summary>poe.ninja's item lists: lines[] = {detailsId: "incandescent-invitation", chaosValue: 87.5} — the same ids as the trade's exchange.</summary>
    public static void ReadStash(string json, Dictionary<string, double> into)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return;
            foreach (var l in lines.EnumerateArray())
                if (l.TryGetProperty("detailsId", out var id) && id.GetString() is { Length: > 0 } i
                    && l.TryGetProperty("chaosValue", out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() > 0)
                    into.TryAdd(i, v.GetDouble());
        }
        catch (JsonException) { }
    }
}

/// <summary>poe.ninja unique prices, one list per kind kept in memory for an hour.</summary>
public sealed class NinjaUniques(HttpClient http)
{
    const string Url = "https://poe.ninja/poe1/api/economy/stash/current/item/overview";
    readonly Dictionary<string, (DateTime At, string Json)> _cache = new();

    /// <summary>Item Class → poe.ninja list; null for things it does not price as uniques.</summary>
    public static string? KindOf(string itemClass) => itemClass switch
    {
        "Helmets" or "Body Armours" or "Gloves" or "Boots" or "Shields" or "Quivers" => "UniqueArmour",
        "Amulets" or "Rings" or "Belts" => "UniqueAccessory",
        "Jewels" or "Abyss Jewels" => "UniqueJewel",
        "Life Flasks" or "Mana Flasks" or "Hybrid Flasks" or "Utility Flasks" => "UniqueFlask",
        _ when PriceCheck.CategoryOf(itemClass)?.StartsWith("weapon", StringComparison.Ordinal) == true => "UniqueWeapon",
        _ => null,
    };

    public async Task<NinjaPrice?> PriceAsync(GameItem item, string league, CancellationToken ct = default)
    {
        if (item.Rarity != ItemRarity.Unique || KindOf(item.ItemClass) is not { } kind) return null;
        try
        {
            var key = league + "|" + kind;
            if (!_cache.TryGetValue(key, out var c) || DateTime.UtcNow - c.At > TimeSpan.FromHours(1))
                _cache[key] = c = (DateTime.UtcNow, await http.GetStringAsync($"{Url}?league={Uri.EscapeDataString(league)}&type={kind}", ct));
            return Find(c.Json, item.Name, item.BaseType, item.Links);
        }
        catch (Exception) { return null; }
    }

    /// <summary>The line for this name and base with the item's links (5L / 6L priced apart, fewer = the plain line).</summary>
    public static NinjaPrice? Find(string json, string name, string baseType, int links)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("lines", out var lines)) return null;
        var want = links >= 5 ? links : 0;
        NinjaPrice? best = null;
        foreach (var l in lines.EnumerateArray())
        {
            if (!l.TryGetProperty("name", out var n) || n.GetString() != name) continue;
            if (l.TryGetProperty("baseType", out var b) && b.GetString() is { Length: > 0 } bt && bt != baseType) continue;
            var lk = l.TryGetProperty("links", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0;
            if (lk != want) continue;
            var chaos = l.TryGetProperty("chaosValue", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetDouble() : 0;
            var div = l.TryGetProperty("divineValue", out var dv) && dv.ValueKind == JsonValueKind.Number ? dv.GetDouble() : 0;
            // a unique with versions has several lines: the cheapest is the honest "from"
            if (best == null || chaos < best.Chaos) best = new NinjaPrice(chaos, div, lk);
        }
        return best;
    }
}
