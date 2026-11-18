using System.Text.Json;
using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class UniqueVariantTests
{
    // The Light of Meaning as PoB keeps it: one bonus of many, picked by "Selected Variant"
    const string Light = """
        Rarity: UNIQUE
        The Light of Meaning
        Prismatic Jewel
        Variant: Life
        Variant: Chaos Damage
        Selected Variant: 2
        Limited to: 1
        Radius: Large
        Implicits: 0
        {variant:1}Passive Skills in Radius also grant +5 to maximum Life
        {variant:2}Passive Skills in Radius also grant 6% increased Chaos Damage
        """;

    const string Stats = """
        {"result":[{"id":"explicit","entries":[
          {"id":"explicit.stat_light_life","text":"Passive Skills in Radius also grant +# to maximum Life"},
          {"id":"explicit.stat_light_chaos","text":"Passive Skills in Radius also grant #% increased Chaos Damage"}]}]}
        """;

    [Fact]
    public void The_chosen_version_of_a_unique_is_part_of_its_trade_search()
    {
        var item = PobItems.ParseItem(1, Light)!;
        var mod = Assert.Single(item.Mods);
        Assert.True(mod.Variant);

        var trade = TradeLink.FromData("Covens", Stats);
        var q = trade.Query("Jewel", item).ToJsonString();
        Assert.Contains("explicit.stat_light_chaos", q);
        Assert.DoesNotContain("explicit.stat_light_life", q);
    }

    [Fact]
    public void Opened_in_the_price_check_the_version_line_is_ticked()
    {
        var item = PobItems.ParseItem(1, Light)!;
        var game = GameItem.Parse(item.ToGameText("Jewels"))!;
        Assert.Equal("The Light of Meaning", game.Name);
        Assert.Contains(game.Mods, m => m.Text == "Passive Skills in Radius also grant 6% increased Chaos Damage");

        var trade = TradeLink.FromData("Covens", Stats);
        var check = PriceCheck.For(game, trade, 90);
        var line = check.Filters.Single(f => f.Label == "Passive Skills in Radius also grant 6% increased Chaos Damage");
        Assert.NotNull(line.StatId);
    }
}
