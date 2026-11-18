using System.Text.Json;
using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class TimelessJewelTests
{
    const string Stats = """
        {"result":[{"id":"explicit","entries":[
          {"id":"explicit.pseudo_timeless_jewel_asenath","text":"Denoted service of # dekhara in the akhara of Asenath"}]}]}
        """;

    [Theory]
    [InlineData("Brutal Restraint", "Denoted service of 2133 dekhara in the akhara of Asenath", 3, 2133, "Asenath")]
    [InlineData("Glorious Vanity", "Bathed in the blood of 7421 sacrificed in the name of Doryani", 1, 7421, "Doryani")]
    [InlineData("Lethal Pride", "Commanded leadership over 18120 warriors under Akoya", 2, 18120, "Akoya")]
    [InlineData("Militant Faith", "Carved to glorify 6411 new faithful converted by High Templar Avarius", 4, 6411, "Avarius")]
    [InlineData("Elegant Hubris", "Commissioned 120420 coins to commemorate Cadiro", 5, 120420, "Cadiro")]
    public void The_seed_line_tells_jewel_seed_and_conqueror(string name, string line, int type, int seed, string conqueror)
    {
        var t = TimelessJewel.From(name, [line])!;
        Assert.Equal((type, seed, conqueror), (t.Type, t.Seed, t.Conqueror));
    }

    [Fact]
    public void The_calculator_opens_on_the_jewel_and_its_socket()
    {
        var t = TimelessJewel.From("Brutal Restraint", ["Denoted service of 2133 dekhara in the akhara of Asenath"])!;
        Assert.Equal("https://vilsol.github.io/timeless-jewels/tree?jewel=3&conqueror=Asenath&seed=2133&location=61834&mode=seed", t.Url(61834));
    }

    [Fact]
    public void Passives_in_the_radius_the_tree_does_not_take_are_greyed_out_on_the_site()
    {
        // socket 1 at the centre; 2 taken, 3 not taken, 4 out of the radius, 5 a mastery
        const string tree = """
            {"constants":{"skillsPerOrbit":[1,6,16,16,40],"orbitRadii":[0,82,162,335,493]},
             "groups":{"1":{"x":0,"y":0},"2":{"x":1000,"y":0},"3":{"x":-900,"y":0},"4":{"x":5000,"y":0},"5":{"x":0,"y":800}},
             "nodes":{"1":{"group":1,"orbit":0,"orbitIndex":0,"isJewelSocket":true},
                      "2":{"group":2,"orbit":0,"orbitIndex":0},
                      "3":{"group":3,"orbit":0,"orbitIndex":0,"isNotable":true},
                      "4":{"group":4,"orbit":0,"orbitIndex":0},
                      "5":{"group":5,"orbit":0,"orbitIndex":0,"isMastery":true}}}
            """;
        var t = PassiveTree.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(tree)));
        Assert.Equal([3], TimelessJewel.NotTaken(t, 1, [1, 2]));
        var j = TimelessJewel.From("Brutal Restraint", ["Denoted service of 2133 dekhara in the akhara of Asenath"])!;
        Assert.EndsWith("&location=1&mode=seed&disabled=3", j.Url(1, [3]));
    }

    [Fact]
    public void The_seed_is_searched_exactly()
    {
        const string jewel = """
            Item Class: Jewels
            Rarity: Unique
            Brutal Restraint
            Timeless Jewel
            --------
            Item Level: 84
            --------
            Denoted service of 2133 dekhara in the akhara of Asenath
            Passives in radius are Conquered by the Maraketh
            Historic
            """;
        var trade = TradeLink.FromData("Covens", Stats);
        var check = PriceCheck.For(GameItem.Parse(jewel)!, trade, 90);
        var seed = check.Filters.Single(f => f.StatId == "explicit.pseudo_timeless_jewel_asenath");
        Assert.True(seed.Enabled);
        Assert.Equal((2133d, 2133d), (seed.Min!.Value, seed.Max!.Value));

        var pob = PobItems.ParseItem(1, "Rarity: UNIQUE\nBrutal Restraint\nTimeless Jewel\nImplicits: 0\n{range:0.5}Denoted service of (500-8000) dekhara in the akhara of Asenath\n")!;
        var q = trade.Query("Jewel", pob).ToJsonString();
        Assert.Contains("\"min\":4250", q);
        Assert.Contains("\"max\":4250", q.Replace(" ", ""));
    }
}
