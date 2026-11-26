using System.Text.RegularExpressions;
using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class MapRegexTests
{
    [Fact]
    public void Every_number_at_least_the_threshold_matches_and_no_smaller_one()
    {
        for (var n = 1; n <= 300; n++)
        {
            // as the game reads it: anywhere in "Item Quantity: +72% (augmented)"
            var rx = new Regex($"tity:.*{MapRegex.AtLeast(n)}%");
            for (var v = 0; v <= 999; v++)
                Assert.True(rx.IsMatch($"Item Quantity: +{v}% (augmented)") == v >= n, $"n={n} v={v} rx={rx}");
        }
    }

    [Fact]
    public void Each_mod_has_a_piece_of_its_own_text()
    {
        var mods = MapRegex.Mods;
        Assert.True(mods.Count > 100);
        foreach (var m in mods)
        {
            Assert.False(string.IsNullOrEmpty(m.Token), m.ToString());
            Assert.Contains(m.Token, MapRegex.Shape(m.ToString()));
            // a piece shared with another mod only when that one contains this whole text
            foreach (var o in mods.Where(o => o != m && o.Lines.Any(l => MapRegex.Shape(l).Contains(m.Token))))
                Assert.Contains(o, m.AlsoMatches);
        }
        // most of them short
        Assert.True(mods.Average(m => m.Token.Length) < 7, string.Join(", ", mods.Select(m => m.Token)));
    }

    [Fact]
    public void A_query_avoids_in_one_group_and_wants_each_or_any()
    {
        var mods = MapRegex.Load(new StringReader("""
            p	r	Monsters are Hexproof
            s	r	Players cannot Regenerate Life, Mana or Energy Shield
            s	r	Area contains a Mirror of Delirium
            s	r	Area contains an Evocator of Beidat
            """));
        var q = new MapRegexQuery { Avoid = { 0, 1 }, Want = { 2, 3 }, Quantity = 80, Corrupted = false, Rare = true };
        var any = MapRegex.Build(q, mods);
        Assert.StartsWith($"\"!{mods[0].Token}|{mods[1].Token}\" \"{mods[2].Token}|{mods[3].Token}\"", any);
        Assert.Contains("\"tity:.*" + MapRegex.AtLeast(80) + "%\"", any);
        Assert.Contains("\"!corrupted\"", any);
        Assert.EndsWith("\"y: r\"", any);

        q.WantAll = true;
        Assert.Contains($" {Quote(mods[2].Token)} {Quote(mods[3].Token)} ", MapRegex.Build(q, mods));
        Assert.Equal("", MapRegex.Build(new MapRegexQuery(), mods));
    }

    static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
