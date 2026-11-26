using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class WarrantTests
{
    // Ctrl+C on a Mercenary Warrant (3.28 Covens)
    const string Warrant = """
        Item Class: Map Fragments
        Rarity: Normal
        Mercenary Warrant
        --------
        Karrak, the Faithful
        --------
        Build: Cardinal
        Mercenary Level: 83
        --------
        Holy Hammers
        Elemental Damage with Attacks (Tier: 2)
        Fist of War (Tier: 3)
        --------
        Holy Strike
        Hallow (Tier: 3)
        Greater Multistrike (Tier: 3)
        --------
        Purity of Lightning
        --------
        Flame Dash
        Cooldown Recovery (Tier: 2)
        --------
        Right click this item to view Mercenary details.
        Open a portal to the Proving Grounds by using this item in a personal Map Device. This previously fought Mercenary will reappear in the area for a rematch. While viewing your Atlas, Right-click this item to pinpoint its location.
        """;

    static TradeLink Trade() => TradeLink.FromData("Covens", """
        {"result":[{"id":"mercenary","label":"Mercenary","entries":[
          {"id":"mercenary.skill_28506","text":"Holy Hammers","type":"mercenary"},
          {"id":"mercenary.skill_38655","text":"Holy Strike","type":"mercenary"},
          {"id":"mercenary.skill_63233","text":"Purity of Lightning","type":"mercenary"},
          {"id":"mercenary.skill_56180","text":"Flame Dash","type":"mercenary"},
          {"id":"mercenary.support_1","text":"Lesser Elemental Damage with Attacks (Tier 1)","type":"mercenary"},
          {"id":"mercenary.support_2","text":"Elemental Damage with Attacks (Tier 2)","type":"mercenary"},
          {"id":"mercenary.support_3","text":"Greater Multistrike (Tier 3)","type":"mercenary"},
          {"id":"mercenary.support_4","text":"Hallow (Tier 3)","type":"mercenary"},
          {"id":"mercenary.support_48875","text":"Cooldown Recovery (Tier 2)","type":"mercenary"}]}]}
        """, """
        {"result":[{"id":"map","label":"Maps","entries":[
          {"type":"Mercenary Warrant"},
          {"type":"AurasMinionsTemplarStaff","text":"Mercenary Warrant (Cardinal)","disc":"mercenary_warrant"}]}]}
        """);

    [Fact]
    public void A_warrant_reads_its_mercenary_build_skills_and_supports()
    {
        var w = GameItem.Parse(Warrant)!.Warrant!;
        Assert.Equal("Karrak, the Faithful", w.Mercenary);
        Assert.Equal("Cardinal", w.Build);
        Assert.Equal(83, w.Level);
        Assert.Equal(["Holy Hammers", "Holy Strike", "Purity of Lightning", "Flame Dash"], w.Skills.Select(s => s.Name));
        Assert.Equal(new MercenarySupport("Hallow", 3), w.Skills[1].Supports[0]);
        Assert.Empty(w.Skills[2].Supports);
        Assert.Equal("Cooldown Recovery (Tier 2)", w.Skills[3].Supports[0].TradeText);
    }

    [Fact]
    public void A_warrant_is_searched_by_its_build_with_every_skill_and_the_supports_one_click_away()
    {
        var trade = Trade();
        var check = PriceCheck.For(GameItem.Parse(Warrant)!, trade, 90);

        Assert.All(check.Filters.Where(f => f.Note == "умение"), f => Assert.True(f.Enabled));
        var supports = check.Filters.Where(f => f.Note != "умение").ToList();
        Assert.Equal(5, supports.Count);
        Assert.All(supports, f => Assert.False(f.Enabled));
        // the exact tier: "Elemental Damage with Attacks (Tier 2)", not the Lesser one of tier 1
        Assert.Equal("mercenary.support_2", supports.Single(f => f.Label.StartsWith("Elemental")).StatId);

        var q = check.Query(trade, new PriceSettings()).ToJsonString();
        Assert.Contains("\"type\":{\"option\":\"AurasMinionsTemplarStaff\",\"discriminator\":\"mercenary_warrant\"}", q);
        Assert.Contains("{\"id\":\"mercenary.skill_38655\"", q);
        Assert.Contains("{\"id\":\"mercenary.support_3\",\"disabled\":true}", q);
    }
}
