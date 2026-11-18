using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FreshExile.Core;

/// <summary>
/// Item art: the game's own inventory icons from GGG's CDN (web.poecdn.com). A unique's own art is found
/// by name in poe.ninja's lists; every other item shows its base's art, whose path comes from RePoE's base
/// item data (the wiki is closed to programs). The name → icon index is kept for a week; every picture is
/// downloaded once and kept on disk.
/// </summary>
public sealed class ItemIcons
{
    const string Ninja = "https://poe.ninja/poe1/api/economy/stash/current/item/overview";
    const string RePoE = "https://repoe-fork.github.io/base_items.json";
    const string Cdn = "https://web.poecdn.com/image/";
    static readonly string[] UniqueTypes = ["UniqueArmour", "UniqueAccessory", "UniqueWeapon", "UniqueFlask", "UniqueJewel"];

    readonly Dictionary<string, string> _uniques;
    readonly Dictionary<string, string> _bases;

    /// <summary>Base names, longest first: "Divine Life Flask" is found in "Concentrated Divine Life Flask of Sealing".</summary>
    readonly string[] _baseNames;

    ItemIcons(Dictionary<string, string> uniques, Dictionary<string, string> bases)
    {
        (_uniques, _bases) = (uniques, bases);
        _baseNames = bases.Keys.OrderByDescending(k => k.Length).ToArray();
    }

    /// <summary>For tests: an index already in hand.</summary>
    public static ItemIcons FromData(Dictionary<string, string> uniques, Dictionary<string, string> bases) =>
        new(new(uniques, StringComparer.OrdinalIgnoreCase), new(bases, StringComparer.OrdinalIgnoreCase));

    /// <summary>RePoE base items → base name → CDN picture ("Art/2DItems/Belts/Belt3.dds" → ".../Belt3.png").</summary>
    public static Dictionary<string, string> ReadBases(string json)
    {
        var bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var b = p.Value;
            var name = b.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var state = b.TryGetProperty("release_state", out var r) ? r.GetString() : "released";
            if (name.Length == 0 || state != "released") continue;
            if (!b.TryGetProperty("visual_identity", out var vi) || !vi.TryGetProperty("dds_file", out var dds)) continue;
            var file = dds.GetString() ?? "";
            if (!file.StartsWith("Art/2DItems/", StringComparison.Ordinal) || !file.EndsWith(".dds", StringComparison.Ordinal)) continue;
            bases.TryAdd(name, Cdn + file[..^4] + ".png");
        }
        return bases;
    }

    /// <summary>
    /// The base of an item: PoB writes a magic item's whole name ("Constant Granite Flask of the Orca"),
    /// so the longest known base inside it is taken.
    /// </summary>
    public string BaseOf(PobItem item)
    {
        if (_bases.ContainsKey(item.BaseType)) return item.BaseType;
        foreach (var b in _baseNames)
        {
            var i = item.BaseType.IndexOf(b, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var end = i + b.Length;
            // whole words only: "Ring" must not match inside "Ringmail"
            if ((i == 0 || item.BaseType[i - 1] == ' ') && (end == item.BaseType.Length || item.BaseType[end] == ' ')) return b;
        }
        return item.BaseType;
    }

    static string Dir => Path.Combine(StateStore.Dir, "icons");
    static string IndexPath => Path.Combine(Dir, "index.json");

    sealed record Index(Dictionary<string, string> Uniques, Dictionary<string, string> Bases);

    public static async Task<ItemIcons> LoadAsync(HttpClient http, string league, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        var fresh = File.Exists(IndexPath) && DateTime.Now - File.GetLastWriteTime(IndexPath) < TimeSpan.FromDays(7);
        if (!fresh)
        {
            try
            {
                var uniques = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var type in UniqueTypes)
                    Read(await http.GetStringAsync($"{Ninja}?league={Uri.EscapeDataString(league)}&type={type}", ct), uniques, byBase: false);
                var bases = ReadBases(await http.GetStringAsync(RePoE, ct));
                await File.WriteAllTextAsync(IndexPath, JsonSerializer.Serialize(new Index(uniques, bases)), ct);
            }
            catch (Exception) when (File.Exists(IndexPath)) { /* offline: last week's index still points at the same pictures */ }
        }
        var index = JsonSerializer.Deserialize<Index>(await File.ReadAllTextAsync(IndexPath, ct))!;
        return new ItemIcons(
            new Dictionary<string, string>(index.Uniques, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(index.Bases, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>poe.ninja lines → name → icon; Foulborn (mutated) versions have their own art and are skipped.</summary>
    public static void Read(string json, Dictionary<string, string> into, bool byBase)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("lines", out var lines)) return;
        foreach (var l in lines.EnumerateArray())
        {
            var name = l.TryGetProperty(byBase ? "baseType" : "name", out var n) ? n.GetString() ?? "" : "";
            var icon = l.TryGetProperty("icon", out var i) ? i.GetString() ?? "" : "";
            if (name.Length == 0 || !icon.StartsWith("https://", StringComparison.Ordinal)) continue;
            if (name.StartsWith("Foulborn ", StringComparison.Ordinal) || icon.Contains("mutated", StringComparison.Ordinal)) continue;
            into.TryAdd(name, icon);
        }
    }

    /// <summary>Icon address for an item: a unique's own art, otherwise its base's; null when poe.ninja does not know it.</summary>
    public string? UrlFor(PobItem item) =>
        (item.Rarity == ItemRarity.Unique && _uniques.TryGetValue(item.Name, out var u) ? u : null)
        ?? (_bases.TryGetValue(BaseOf(item), out var b) ? b : null);

    /// <summary>A raw flask picture is a strip of three frames (full, empty, fill): only the first is the item.</summary>
    public static bool IsFlaskStrip(string url) =>
        url.StartsWith(Cdn + "Art/2DItems/Flasks/", StringComparison.Ordinal);

    /// <summary>The picture on disk, downloaded the first time it is asked for.</summary>
    public static async Task<string?> FileFor(HttpClient http, string url, CancellationToken ct = default)
    {
        var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant() + ".png";
        var path = Path.Combine(Dir, name);
        if (File.Exists(path)) return path;
        try
        {
            var bytes = await http.GetByteArrayAsync(url, ct);
            Directory.CreateDirectory(Dir);
            await File.WriteAllBytesAsync(path + ".tmp", bytes, ct);
            File.Move(path + ".tmp", path, overwrite: true);
            return path;
        }
        catch (Exception) { return null; }
    }
}
