using System.Text.Json;

namespace FreshExile.Core;

/// <summary>A sprite in one of the export's sheets: where it sits in the picture, in pixels.</summary>
public readonly record struct SpriteRect(int X, int Y, int W, int H);

/// <summary>
/// One of the export's sprite sheets at its sharpest zoom: the picture on GGG's CDN and every sprite in it by name
/// ("Art/2DArt/SkillIcons/passives/AreaDmgNode.png", "PSSkillFrame"). <see cref="Zoom"/> turns pixels into tree units:
/// a sprite w pixels wide covers w / Zoom of the tree, the size the game draws it at.
/// </summary>
public sealed record SpriteSheet(string Url, double Zoom, IReadOnlyDictionary<string, SpriteRect> Coords);

public sealed partial class PassiveTree
{
    /// <summary>The export's sprite sheets by name ("normalActive", "notableActive", "frame", "jewel"…).</summary>
    public IReadOnlyDictionary<string, SpriteSheet> Sprites { get; private set; } = new Dictionary<string, SpriteSheet>();

    static Dictionary<string, SpriteSheet> ReadSprites(JsonElement root)
    {
        var sheets = new Dictionary<string, SpriteSheet>();
        if (!root.TryGetProperty("sprites", out var sprites) || sprites.ValueKind != JsonValueKind.Object) return sheets;
        foreach (var sheet in sprites.EnumerateObject())
        {
            if (sheet.Value.ValueKind != JsonValueKind.Object) continue;
            // the zoom levels are named by their scale: the biggest is the sharpest
            JsonElement best = default;
            var zoom = 0.0;
            foreach (var z in sheet.Value.EnumerateObject())
                if (double.TryParse(z.Name, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > zoom)
                    (zoom, best) = (v, z.Value);
            if (zoom <= 0 || Str(best, "filename") is not { Length: > 0 } url || !best.TryGetProperty("coords", out var coords)) continue;
            var map = new Dictionary<string, SpriteRect>(StringComparer.Ordinal);
            foreach (var c in coords.EnumerateObject())
                map[c.Name] = new SpriteRect(Int(c.Value, "x"), Int(c.Value, "y"), Int(c.Value, "w"), Int(c.Value, "h"));
            sheets[sheet.Name] = new SpriteSheet(url, zoom, map);
        }
        return sheets;
    }
}
