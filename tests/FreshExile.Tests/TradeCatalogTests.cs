using System.Text.Json;
using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

/// <summary>The search window: names from the trade's lists, and what each one searches.</summary>
public class TradeCatalogTests
{
    const string Items = """
        {"result":[
          {"id":"map","label":"Maps","entries":[
            {"type":"Shaper Guardian Map"},{"type":"Map","disc":"map"},{"type":"Valdo Map"},
            {"type":"Nightmare Map"},{"type":"Shaper Guardian Map","name":"Pillars of Vastiri","flags":{"unique":true}}]},
          {"id":"accessory","label":"Accessories","entries":[
            {"type":"Heavy Belt","name":"Mageblood","flags":{"unique":true}},
            {"type":"Unset Ring","text":"Malachai's Artifice Unset Ring","name":"Malachai's Artifice","flags":{"unique":true}}]},
          {"id":"gem","label":"Gems","entries":[{"type":"Cleave"},{"type":"Cleave","text":"Cleave of Rage","disc":"alt_x"}]},
          {"id":"currency","label":"Currency","entries":[{"type":"Divine Orb"}]}
        ]}
        """;

    const string Static = """
        {"result":[{"id":"Currency","label":"Currency","entries":[{"id":"divine","text":"Divine Orb","image":"/gen/image/divine.png"}]}]}
        """;

    static TradeLink Trade() => TradeLink.FromData("Covens", null, Items, Static);

    [Fact]
    public void FindsByWordsClosestFirst()
    {
        var trade = Trade();
        Assert.Equal("Shaper Guardian Map", trade.FindNames("shaper guardian")[0].Text);
        Assert.Equal("Shaper Guardian Map", trade.FindNames("guard shap")[0].Text);
        Assert.Equal("Mageblood Heavy Belt", Assert.Single(trade.FindNames("mageblood")).Text);
        Assert.Empty(trade.FindNames("  "));
        Assert.Empty(trade.FindNames("headhunter"));
        // the site's generic "Map" (disc) is not a name of its own
        Assert.DoesNotContain(trade.Catalog, e => e.Text == "Map");
    }

    [Fact]
    public void ExchangeNameWinsOverTheSameBase()
    {
        var divine = Assert.Single(Trade().FindNames("divine"));
        Assert.Equal(TradeEntryKind.Exchange, divine.Kind);
        Assert.Equal("divine", Trade().ExchangeId(divine.ToItem()));
    }

    [Fact]
    public void BaseSearchesByType()
    {
        var trade = Trade();
        var entry = trade.FindNames("shaper guardian map")[0];
        Assert.Equal(TradeEntryKind.Item, entry.Kind);
        var item = entry.ToItem();
        Assert.Null(trade.ExchangeId(item));
        var q = Query(trade, item);
        Assert.Equal("Shaper Guardian Map", q.GetProperty("type").GetString());
        Assert.False(q.TryGetProperty("name", out _));
    }

    [Fact]
    public void UniqueSearchesByNameAndBase()
    {
        var trade = Trade();
        var q = Query(trade, trade.FindNames("malachai")[0].ToItem());
        Assert.Equal("Malachai's Artifice", q.GetProperty("name").GetString());
        Assert.Equal("Unset Ring", q.GetProperty("type").GetString());
    }

    [Fact]
    public void TransfiguredGemSearchesUnderItsBase()
    {
        var trade = Trade();
        var item = trade.FindNames("cleave of rage")[0].ToItem();
        Assert.True(item.IsGem);
        Assert.Equal("Cleave", Query(trade, item).GetProperty("type").GetProperty("option").GetString());
    }

    static JsonElement Query(TradeLink trade, GameItem item) =>
        JsonDocument.Parse(PriceCheck.For(item, trade, 80).Query(trade, new PriceSettings()).ToJsonString()).RootElement.GetProperty("query");
}
