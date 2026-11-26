using System.Globalization;
using System.Net;
using System.Text.Json;

namespace FreshExile.Core;

public sealed record CharacterSnapshot(string Name, int Level, string Class, IReadOnlyList<int> Nodes, IReadOnlySet<string> GemKeys)
{
    /// <summary>The effect the character chose on each of its masteries: mastery node → effect id.</summary>
    public IReadOnlyDictionary<int, int> MasteryEffects { get; init; } = new Dictionary<int, int>();

    /// <summary>Runegrafts and tattoos the character has on its tree: node → name ("Runegraft of the Warp").</summary>
    public IReadOnlyDictionary<int, string> Overrides { get; init; } = new Dictionary<int, string>();

    public bool HasGem(Gem g) => GemKeys.Contains(g.Key) || GemKeys.Contains(Gem.GemKey(g.Name + " Support"));
}

public sealed class PoeApiException(string message) : Exception(message);

/// <summary>Public character profile (pathofexile.com character-window). Works only for public profiles.</summary>
public sealed class PoeApi(HttpClient http)
{
    const string Base = "https://www.pathofexile.com/character-window/";

    public async Task<CharacterSnapshot> GetCharacterAsync(string account, string character, CancellationToken ct = default)
    {
        account = account.Trim();
        character = character.Trim();
        if (account.Length == 0 || character.Length == 0) throw new PoeApiException(Loc.T("Укажи аккаунт и персонажа"));

        using var passives = await PostAsync("get-passive-skills", account, character, ct);
        using var items = await PostAsync("get-items", account, character, ct);

        var nodes = ReadNodes(passives.RootElement);
        Trace?.Invoke(Describe(passives.RootElement));

        var gems = new HashSet<string>();
        var root = items.RootElement;
        if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.TryGetProperty("socketedItems", out var socketed) && socketed.ValueKind == JsonValueKind.Array)
                    foreach (var s in socketed.EnumerateArray())
                        if (s.TryGetProperty("typeLine", out var tl) && tl.GetString() is { } name)
                            gems.Add(Gem.GemKey(name));

        string name1 = character, cls = "";
        var level = 0;
        if (root.TryGetProperty("character", out var c))
        {
            if (c.TryGetProperty("name", out var n)) name1 = n.GetString() ?? character;
            if (c.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number) level = l.GetInt32();
            if (c.TryGetProperty("class", out var k)) cls = k.GetString() ?? "";
        }
        return new CharacterSnapshot(name1, level, cls, nodes, gems)
        {
            MasteryEffects = ReadMasteryEffects(passives.RootElement),
            Overrides = ReadOverrides(passives.RootElement),
        };
    }

    /// <summary>
    /// The allocated passives: "hashes" (the tree) and "hashes_ex" (the passives inside socketed cluster jewels, ids from
    /// 65536 the way PoB numbers them) and the masteries of "mastery_effects" ({"node": effect}, or a list of
    /// effect &lt;&lt; 16 | node). Without these a cluster's notables and a chosen mastery showed as still to take.
    /// </summary>
    public static List<int> ReadNodes(JsonElement root)
    {
        var nodes = new List<int>();
        foreach (var key in (string[])["hashes", "hashes_ex"])
            if (root.TryGetProperty(key, out var h) && h.ValueKind == JsonValueKind.Array)
                nodes.AddRange(h.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()));
        if (root.TryGetProperty("mastery_effects", out var me))
        {
            if (me.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in me.EnumerateObject())
                    if (int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var node)) nodes.Add(node);
            }
            else if (me.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in me.EnumerateArray())
                    if (Number(x) is long v) nodes.Add((int)(v & 0xFFFF));
            }
        }
        return nodes.Distinct().ToList();
    }

    /// <summary>
    /// The effect chosen on each mastery: "mastery_effects" as {"node": effect} or as a list of effect &lt;&lt; 16 | node.
    /// </summary>
    public static Dictionary<int, int> ReadMasteryEffects(JsonElement root)
    {
        var map = new Dictionary<int, int>();
        if (!root.TryGetProperty("mastery_effects", out var me)) return map;
        if (me.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in me.EnumerateObject())
                if (int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var node) && Number(p.Value) is long eff)
                    map[node] = (int)eff;
        }
        else if (me.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in me.EnumerateArray())
                if (Number(x) is long v) map[(int)(v & 0xFFFF)] = (int)(v >> 16);
        }
        return map;
    }

    /// <summary>"skill_overrides": {"node": {"name": "Tattoo of …", "stats": […]}} — the runegrafts and tattoos on the tree.</summary>
    public static Dictionary<int, string> ReadOverrides(JsonElement root)
    {
        var map = new Dictionary<int, string>();
        if (!root.TryGetProperty("skill_overrides", out var so) || so.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in so.EnumerateObject())
            if (int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var node))
                map[node] = p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        return map;
    }

    /// <summary>Where the log goes (the app's overlay.log): what the profile's passives answer holds.</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>"passives: hashes[114] hashes_ex[12] mastery_effects{…}": the shape of the answer, no account data.</summary>
    static string Describe(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return $"passives: {root.ValueKind}";
        var parts = root.EnumerateObject().Select(p => p.Value.ValueKind switch
        {
            JsonValueKind.Array => $"{p.Name}[{p.Value.GetArrayLength()}]",
            JsonValueKind.Object => $"{p.Name}{{{p.Value.EnumerateObject().Count()}}}",
            _ => p.Name,
        });
        var me = root.TryGetProperty("mastery_effects", out var m) ? m.GetRawText() : "none";
        return $"passives: {string.Join(" ", parts)}; mastery_effects = {(me.Length > 400 ? me[..400] + "…" : me)}";
    }

    static long? Number(JsonElement x) =>
        x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var v) ? v
        : x.ValueKind == JsonValueKind.String && long.TryParse(x.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s
        : null;

    async Task<JsonDocument> PostAsync(string endpoint, string account, string character, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["accountName"] = account,
            ["character"] = character,
            ["realm"] = "pc",
        });
        using var resp = await http.PostAsync(Base + endpoint, content, ct);
        switch (resp.StatusCode)
        {
            case HttpStatusCode.Forbidden:
                throw new PoeApiException(Loc.T("Профиль приватный. Открой вкладку Characters в настройках приватности на pathofexile.com"));
            case HttpStatusCode.NotFound:
                throw new PoeApiException(Loc.T("Аккаунт или персонаж не найден (аккаунт пишется с #1234)"));
            case HttpStatusCode.TooManyRequests:
                throw new PoeApiException(Loc.T("Слишком часто, сайт GGG просит подождать минуту"));
        }
        if (!resp.IsSuccessStatusCode) throw new PoeApiException(Loc.T($"pathofexile.com ответил {(int)resp.StatusCode}"));

        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err))
        {
            doc.Dispose();
            throw new PoeApiException(err.TryGetProperty("message", out var m) ? m.GetString() ?? Loc.T("Ошибка API") : Loc.T("Ошибка API"));
        }
        return doc;
    }
}
