using System.Text.Json;

namespace FreshExile.Core;

public enum GemColour { None, Red, Green, Blue }

public sealed record GemInfo(string Id, string Name, GemColour Colour, int RequiredLevel, bool IsSupport);

/// <summary>Where a class gets a gem first: quest reward (free) or a town vendor.</summary>
public sealed record GemSource(int Act, string Quest, string Npc, bool IsReward);

/// <summary>Gem colours, required levels and quest/vendor availability (data: HeartofPhos/exile-leveling, MIT).</summary>
public sealed class GemDatabase
{
    readonly Dictionary<string, GemInfo> _byId = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, GemInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<(GemSource Source, string[] Classes)>> _sources = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byId.Count;

    /// <summary>Every gem name as the game shows it ("Volley Support").</summary>
    public IEnumerable<string> Names => _byId.Values.Select(g => g.Name).Where(n => n.Length > 0).Distinct();

    public static GemDatabase Load(Stream gemsJson, Stream questsJson)
    {
        var db = new GemDatabase();

        using (var gems = JsonDocument.Parse(gemsJson))
        {
            foreach (var p in gems.RootElement.EnumerateObject())
            {
                var g = p.Value;
                var name = Str(g, "name") ?? "";
                var colour = Str(g, "primary_attribute") switch
                {
                    "strength" => GemColour.Red,
                    "dexterity" => GemColour.Green,
                    "intelligence" => GemColour.Blue,
                    _ => GemColour.None,
                };
                var info = new GemInfo(p.Name, name, colour,
                    g.TryGetProperty("required_level", out var rl) && rl.ValueKind == JsonValueKind.Number ? rl.GetInt32() : 1,
                    g.TryGetProperty("is_support", out var s) && s.ValueKind == JsonValueKind.True);
                db._byId[p.Name] = info;
                // first entry wins: later ids with the same name are event / royale variants
                db._byName.TryAdd(name, info);
                if (name.EndsWith(" Support", StringComparison.Ordinal)) db._byName.TryAdd(name[..^8], info);
            }
        }

        using (var quests = JsonDocument.Parse(questsJson))
        {
            foreach (var q in quests.RootElement.EnumerateObject())
            {
                var act = int.TryParse(Str(q.Value, "act"), out var a) ? a : 99;
                var questName = Str(q.Value, "name") ?? q.Name;
                if (!q.Value.TryGetProperty("reward_offers", out var offers) || offers.ValueKind != JsonValueKind.Object) continue;

                foreach (var offer in offers.EnumerateObject())
                {
                    var o = offer.Value;
                    var questNpc = Str(o, "quest_npc") ?? "";
                    if (o.TryGetProperty("quest", out var rewards) && rewards.ValueKind == JsonValueKind.Object)
                        foreach (var r in rewards.EnumerateObject())
                            db.Add(r.Name, new GemSource(act, questName, questNpc, true), Classes(r.Value));
                    if (o.TryGetProperty("vendor", out var vendor) && vendor.ValueKind == JsonValueKind.Object)
                        foreach (var v in vendor.EnumerateObject())
                            db.Add(v.Name, new GemSource(act, questName, Str(v.Value, "npc") ?? questNpc, false), Classes(v.Value));
                }
            }
        }
        return db;
    }

    void Add(string gemId, GemSource source, string[] classes)
    {
        if (!_sources.TryGetValue(gemId, out var list)) _sources[gemId] = list = new();
        list.Add((source, classes));
    }

    public GemInfo? Find(Gem gem) =>
        (gem.GemId.Length > 0 && _byId.TryGetValue(gem.GemId, out var byId) ? byId : null)
        ?? (_byName.TryGetValue(gem.Name, out var byName) ? byName : null);

    /// <summary>
    /// Transfigured gem ("Poisonous Concoction of Bouncing"): PoB keeps the base gem's gemId on it, so it
    /// is told by the skillId ending in AltX/AltY/AltZ or by a name the base gem does not have.
    /// Vendors do not sell it: the base gem is turned into it at the Divine Font in the Labyrinth.
    /// </summary>
    public bool IsTransfigured(Gem gem)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(gem.SkillId, "Alt[XYZ]$")) return true;
        if (gem.Name == gem.SkillId) return false; // no nameSpec: nothing to compare
        var info = Find(gem);
        return info != null && !string.Equals(StripSupport(gem.Name), StripSupport(info.Name), StringComparison.OrdinalIgnoreCase);
    }

    static string StripSupport(string name) =>
        name.EndsWith(" Support", StringComparison.OrdinalIgnoreCase) ? name[..^8].Trim() : name.Trim();

    /// <summary>
    /// Earliest act where this class can get the gem; within one act a quest reward (free) beats a vendor.
    /// Empty class list in the data means every class.
    /// </summary>
    public GemSource? WhereToGet(Gem gem, string className)
    {
        var info = Find(gem);
        if (info == null || !_sources.TryGetValue(info.Id, out var list)) return null;

        GemSource? best = null;
        foreach (var (src, classes) in list)
        {
            if (classes.Length > 0 && !classes.Contains(className, StringComparer.OrdinalIgnoreCase)) continue;
            if (best == null || src.Act < best.Act || (src.Act == best.Act && src.IsReward && !best.IsReward)) best = src;
        }
        return best;
    }

    static string[] Classes(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("classes", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
            : [];

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public static class GemRepository
{
    const string Base = "https://raw.githubusercontent.com/HeartofPhos/exile-leveling/main/common/data/json/";

    static string Dir => Path.Combine(StateStore.Dir, "gems");

    public static async Task<GemDatabase> LoadAsync(HttpClient http, bool forceDownload = false, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        foreach (var name in new[] { "gems.json", "quests.json" })
        {
            var path = Path.Combine(Dir, name);
            if (!forceDownload && File.Exists(path)) continue;
            var bytes = await http.GetByteArrayAsync(Base + name, ct);
            await File.WriteAllBytesAsync(path + ".tmp", bytes, ct);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        return await Task.Run(() =>
        {
            using var g = File.OpenRead(Path.Combine(Dir, "gems.json"));
            using var q = File.OpenRead(Path.Combine(Dir, "quests.json"));
            return GemDatabase.Load(g, q);
        }, ct);
    }
}
