using System.Globalization;
using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>
/// A timeless jewel's seed line ("Denoted service of 2133 dekhara in the akhara of Asenath"): which jewel, its seed and
/// conqueror. What it does to the tree around its socket is a calculation of its own: Vilsol's timeless-jewels site does it,
/// opened right on the jewel and the socket.
/// </summary>
public sealed partial record TimelessJewel(int Type, string Name, int Seed, string Conqueror)
{
    /// <summary>Jewel numbers as the site counts them (data/jewels.go).</summary>
    static readonly (string Name, int Type)[] Types =
    [
        ("Glorious Vanity", 1), ("Lethal Pride", 2), ("Brutal Restraint", 3), ("Militant Faith", 4), ("Elegant Hubris", 5), ("Heroic Tragedy", 6),
    ];

    /// <summary>Null when no line of the item is a timeless seed line.</summary>
    public static TimelessJewel? From(string name, IEnumerable<string> lines)
    {
        var type = Types.FirstOrDefault(t => name.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
        if (type.Name == null) return null;
        foreach (var l in lines)
        {
            var m = SeedRx().Match(l);
            if (m.Success)
                return new TimelessJewel(type.Type, type.Name, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value);
        }
        return null;
    }

    /// <summary>A seed line: searched at exactly its number, another seed is another jewel.</summary>
    public static bool IsSeedLine(string line) => SeedRx().IsMatch(line);

    /// <summary>The site's jewel radius around the socket, in tree units (skill_tree.ts baseJewelRadius).</summary>
    public const double Radius = 1800;

    /// <summary>
    /// The site on this jewel; with the socket's node id, on the tree around it. <paramref name="disabled"/>: nodes greyed out
    /// there, as a click on them would (the ones the build does not take, so only what it gets stands out).
    /// </summary>
    public string Url(int? socket = null, IEnumerable<int>? disabled = null) =>
        $"https://vilsol.github.io/timeless-jewels/tree?jewel={Type}&conqueror={Uri.EscapeDataString(Conqueror)}&seed={Seed}"
        + (socket is int s ? $"&location={s}" : "") + "&mode=seed"
        + string.Concat((disabled ?? []).Select(d => $"&disabled={d}"));

    /// <summary>Passives in the jewel's radius the tree does not take (sockets, masteries and the like left alone).</summary>
    public static IReadOnlyList<int> NotTaken(PassiveTree tree, int socket, IReadOnlyCollection<int> taken)
    {
        if (!tree.Nodes.TryGetValue(socket, out var s)) return [];
        var have = taken as ISet<int> ?? taken.ToHashSet();
        return tree.Nodes.Values
            .Where(n => n.Drawable && n.Kind is NodeKind.Normal or NodeKind.Notable or NodeKind.Keystone && !have.Contains(n.Id)
                        && Math.Sqrt((n.X - s.X) * (n.X - s.X) + (n.Y - s.Y) * (n.Y - s.Y)) < Radius)
            .Select(n => n.Id).OrderBy(x => x).ToList();
    }

    [GeneratedRegex(@"^(?:Bathed in the blood of|Commanded leadership over|Denoted service of|Carved to glorify|Commissioned|Remembrancing) (\d+) .*?(?:of|under|by High Templar|commemorate|line of) (\w+)$")]
    private static partial Regex SeedRx();
}
