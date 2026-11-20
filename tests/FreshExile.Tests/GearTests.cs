using System.Xml.Linq;
using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class GearTests
{
    const string Rare = """
        Rarity: RARE
        Doom Thirst
        Leather Belt
        Unique ID: abc
        Item Level: 80
        Quality: 0
        LevelReq: 60
        Implicits: 1
        {tags:life}+30 to maximum Life
        {range:0.5}+(40-50) to maximum Life
        +35% to Fire Resistance
        {crafted}+20% to Cold Resistance
        Corrupted
        """;

    const string Unique = """
        Rarity: UNIQUE
        Goldrim
        Leather Cap
        Selected Variant: 2
        Implicits: 0
        {variant:1}+(30-50) to Evasion Rating
        {variant:2}+(50-100) to Evasion Rating
        10% increased Rarity of Items found
        """;

    [Fact]
    public void Rare_item_mods_are_plain_game_lines()
    {
        var item = PobItems.ParseItem(1, Rare)!;
        Assert.Equal(ItemRarity.Rare, item.Rarity);
        Assert.Equal("Doom Thirst", item.Name);
        Assert.Equal("Leather Belt", item.BaseType);
        Assert.True(item.Corrupted);
        Assert.Equal(
            [("+30 to maximum Life", ModKind.Implicit), ("+45 to maximum Life", ModKind.Explicit),
             ("+35% to Fire Resistance", ModKind.Explicit), ("+20% to Cold Resistance", ModKind.Crafted)],
            item.Mods.Select(m => (m.Text, m.Kind)));
    }

    [Fact]
    public void Unique_keeps_only_the_selected_variant()
    {
        var item = PobItems.ParseItem(2, Unique)!;
        Assert.Equal(["+75 to Evasion Rating", "10% increased Rarity of Items found"], item.Mods.Select(m => m.Text));
    }

    [Fact]
    public void Item_sets_follow_stage_titles()
    {
        var xml = $"""
            <PathOfBuilding>
              <Build className="Ranger" ascendClassName="Pathfinder" level="60"/>
              <Tree><Spec title="Act 3" nodes=""/><Spec title="Act 8" nodes=""/></Tree>
              <Items activeItemSet="2">
                <Item id="1">{Unique}</Item>
                <Item id="2">{Rare}<ModRange id="1" range="0.5"/></Item>
                <ItemSet id="1" title="Act 3"><Slot name="Helmet" itemId="1"/></ItemSet>
                <ItemSet id="2" title="Act 7+"><Slot name="Belt" itemId="2"/><Slot name="Weapon 1 Swap" itemId="1"/></ItemSet>
              </Items>
            </PathOfBuilding>
            """;
        var build = BuildParser.Parse(xml);
        Assert.Equal("Goldrim", build.GearFor(build.Stages[0])!.Value.Set.Slots.Single().Item.Name);
        var act8 = build.GearFor(build.Stages[1])!.Value;
        Assert.True(act8.Exact);
        Assert.Equal("Belt", act8.Set.Slots.Single().Slot);
    }

    [Fact]
    public void Icons_come_from_unique_art_or_the_base_hidden_in_a_magic_name()
    {
        const string repoe = """
            {"a":{"name":"Divine Life Flask","release_state":"released","visual_identity":{"dds_file":"Art/2DItems/Flasks/lifeflask11.dds"}},
             "b":{"name":"Leather Cap","release_state":"released","visual_identity":{"dds_file":"Art/2DItems/Armours/Helmets/HelmetDex1.dds"}},
             "c":{"name":"Ring","release_state":"unreleased","visual_identity":{"dds_file":"Art/2DItems/Rings/Ring1.dds"}},
             "d":{"name":"Ringmail Coat","release_state":"released","visual_identity":{"dds_file":"Art/2DItems/Armours/BodyArmours/BodyStrDex1.dds"}}}
            """;
        var bases = ItemIcons.ReadBases(repoe);
        Assert.Equal("https://web.poecdn.com/image/Art/2DItems/Flasks/lifeflask11.png", bases["Divine Life Flask"]);
        Assert.False(bases.ContainsKey("Ring"));

        var icons = ItemIcons.FromData(new() { ["Goldrim"] = "https://web.poecdn.com/gen/image/x/Goldrim.png" }, bases);
        Assert.Equal("https://web.poecdn.com/gen/image/x/Goldrim.png", icons.UrlFor(PobItems.ParseItem(2, Unique)!));

        var flask = PobItems.ParseItem(3, "Rarity: MAGIC\nConcentrated Divine Life Flask of Sealing\nImplicits: 0\n")!;
        Assert.Equal("Divine Life Flask", icons.BaseOf(flask));
        Assert.True(ItemIcons.IsFlaskStrip(icons.UrlFor(flask)!));

        var coat = PobItems.ParseItem(4, "Rarity: MAGIC\nSturdy Ringmail Coat of the Lynx\nImplicits: 0\n")!;
        Assert.Equal("Ringmail Coat", icons.BaseOf(coat));
    }

    [Fact]
    public void Gems_search_by_trade_type_transfigured_under_its_base()
    {
        const string items = """
            {"result":[{"id":"gem","label":"Gems","entries":[
              {"type":"Poisonous Concoction"},{"type":"Unbound Ailments Support"},
              {"type":"Poisonous Concoction","text":"Poisonous Concoction of Bouncing","disc":"alt_x"}]}]}
            """;
        var trade = TradeLink.FromData("Covens", null, items);
        // the gem row's button: the gem as the game would copy it, through the price check's query
        string Q(string name) => PriceCheck.For(GameItem.Parse(GemSearch.ItemText(name, false, 1))!, trade, 90)
            .Query(trade, new PriceSettings()).ToJsonString();

        Assert.Contains("\"type\":{\"option\":\"Poisonous Concoction\",\"discriminator\":\"alt_x\"}", Q("Poisonous Concoction of Bouncing"));
        Assert.Contains("\"type\":\"Unbound Ailments Support\"", Q("Unbound Ailments"));
        Assert.Contains("\"type\":\"Poisonous Concoction\"", Q("Poisonous Concoction"));
        Assert.Contains("\"option\":\"securable\"", Q("Poisonous Concoction"));
    }

    [Fact]
    public void Item_supports_its_sockets_and_the_link_knows_its_slot()
    {
        const string boots = """
            Rarity: UNIQUE
            March of the Legion
            Legion Boots
            Selected Variant: 2
            Implicits: 0
            {variant:1}+2 to Level of Socketed Aura Gems
            {variant:2}{range:0.5}+(3-5) to Level of Socketed Aura Gems
            Socketed Gems are Supported by Level 25 Divine Blessing
            """;
        var item = PobItems.ParseItem(1, boots)!;
        Assert.Equal([("Divine Blessing", 25)], item.GrantedSupports);
        Assert.Equal(["+4 to Level of Socketed Aura Gems"], item.SocketedBonuses);

        var build = BuildParser.Parse("""
            <PathOfBuilding><Build className="Ranger" level="60"/>
              <Skills><Skill slot="Boots" enabled="true"><Gem nameSpec="Malevolence" skillId="Malevolence" level="20"/></Skill></Skills>
            </PathOfBuilding>
            """);
        Assert.Equal("Boots", build.Stages[0].Groups.Single().Slot);
    }

    [Fact]
    public void League_is_the_current_softcore_trade_league()
    {
        const string json = """{"result":[{"id":"Standard","realm":"pc"},{"id":"Hardcore","realm":"pc"},{"id":"Keepers","realm":"pc"},{"id":"Hardcore Keepers","realm":"pc"},{"id":"SSF Keepers","realm":"pc"}]}""";
        Assert.Equal("Keepers", TradeLink.PickLeague(json));
        Assert.Equal("Standard", TradeLink.PickLeague("not json"));
    }

    [Fact]
    public void Unique_searches_by_name_and_rare_by_slot_and_mods()
    {
        const string stats = """
            {"result":[
              {"id":"explicit","entries":[{"id":"explicit.stat_3299347043","text":"+# to maximum Life"},{"id":"explicit.stat_3372524247","text":"+#% to Fire Resistance"},{"id":"explicit.stat_4220027924","text":"+#% to Cold Resistance"}]},
              {"id":"crafted","entries":[{"id":"crafted.stat_4220027924","text":"+#% to Cold Resistance"}]}
            ]}
            """;
        var trade = TradeLink.FromData("Keepers", stats);

        var u = trade.Query("Helmet", PobItems.ParseItem(2, Unique)!).ToJsonString();
        Assert.Contains("\"status\":{\"option\":\"securable\"}", u); // Instant Buyout unless asked otherwise
        Assert.Contains("\"name\":\"Goldrim\"", u);
        trade.Status = "online";
        Assert.Contains("\"option\":\"online\"", trade.Query("Helmet", PobItems.ParseItem(2, Unique)!).ToJsonString());
        trade.Status = TradeLink.DefaultStatus;
        Assert.Contains("\"type\":\"Leather Cap\"", u);

        var r = trade.Query("Belt", PobItems.ParseItem(1, Rare)!).ToJsonString();
        Assert.Contains("accessory.belt", r);
        Assert.Contains("\"id\":\"explicit.stat_3299347043\",\"value\":{\"min\":36}", r); // 80% of 45
        Assert.Contains("crafted.stat_4220027924", r);

        // a unique's rolled lines get minimums by the slider; fixed lines do not; 0 = by name only
        var shield = PobItems.ParseItem(5, "Rarity: UNIQUE\nLeper's Alms\nMirrored Spiked Shield\nImplicits: 0\n+(60-80) to maximum Life\n+35% to Fire Resistance\n")!;
        trade.Strictness = 50;
        var s = trade.Query("Weapon 2", shield).ToJsonString();
        Assert.Contains("\"id\":\"explicit.stat_3299347043\",\"value\":{\"min\":35}", s);
        Assert.DoesNotContain("3372524247", s);
        trade.Strictness = 0;
        Assert.DoesNotContain("stats", trade.Query("Weapon 2", shield).ToJsonString());
        trade.Strictness = TradeLink.DefaultStrictness;

        var url = trade.Url("Belt", PobItems.ParseItem(1, Rare)!);
        Assert.StartsWith("https://www.pathofexile.com/trade/search/Keepers?q=", url);
    }

    [Fact]
    public void Flask_is_searched_by_its_base_not_any_flask()
    {
        var flask = new PobItem(1, ItemRarity.Magic, "Analyst's Bismuth Flask of the Rainbow", "Analyst's Bismuth Flask of the Rainbow",
            [new ItemMod("19% additional Elemental Resistances during Effect", ModKind.Explicit)]);
        var q = TradeLink.FromData("Covens", null).Query("Flask 3", flask, "Bismuth Flask")["query"]!;
        Assert.Equal("Bismuth Flask", (string?)q["type"]);
        Assert.Null(q["filters"]?["type_filters"]);
    }

    [Fact]
    public void EldritchImplicitsKeepTheirMaster()
    {
        var item = PobItems.ParseItem(1, """
            Rarity: RARE
            Golem Stride
            Slink Boots
            Searing Exarch Item
            Eater of Worlds Item
            Shaper Item
            Implicits: 2
            {eater}While a Unique Enemy is in your Presence, Poisons you inflict deal Damage 9% Faster
            {tags:speed}{exarch}{range:0.5}(3-5)% increased Action Speed
            +115 to maximum Life
            """)!;
        Assert.Equal([Influence.Exarch, Influence.Eater, Influence.Shaper], item.Influences);
        Assert.Equal(Influence.Eater, item.Mods[0].Influence);
        Assert.Equal(Influence.Exarch, item.Mods[1].Influence);
        Assert.Equal("4% increased Action Speed", item.Mods[1].Text);
        Assert.Null(item.Mods[2].Influence);
    }

    [Fact]
    public void UntaggedModsFindTheirMasterByText()
    {
        // PoB wrote neither {exarch} nor {eater}: the game's mod pools tell the two apart, and a conqueror's explicit too
        var item = PobItems.ParseItem(1, """
            Rarity: RARE
            Golem Stride
            Slink Boots
            Searing Exarch Item
            Eater of Worlds Item
            Hunter Item
            Implicits: 2
            While a Unique Enemy is in your Presence, Poisons you inflict deal Damage 9% Faster
            4% increased Action Speed
            12% chance to Impale Enemies on Hit with Attacks
            +115 to maximum Life
            {crafted}+19% to Fire and Lightning Resistances
            """)!;
        Assert.Equal(Influence.Eater, item.Mods[0].Influence);
        Assert.Equal(Influence.Exarch, item.Mods[1].Influence);
        Assert.Equal(Influence.Hunter, item.Mods[2].Influence);
        Assert.Null(item.Mods[3].Influence);
        Assert.Null(item.Mods[4].Influence);
    }

    [Fact]
    public void AnInfluenceTheItemLacksIsNotGuessed()
    {
        // the Hunter's line on an item with no Hunter: a plain item never gets painted
        var item = PobItems.ParseItem(1, """
            Rarity: RARE
            Golem Stride
            Slink Boots
            Implicits: 0
            12% chance to Impale Enemies on Hit with Attacks
            """)!;
        Assert.Null(item.Mods[0].Influence);
    }

    [Fact]
    public void UntaggedImplicitTakesTheOnlyEldritchMaster()
    {
        var item = PobItems.ParseItem(1, """
            Rarity: RARE
            Doom Shell
            Vaal Regalia
            Searing Exarch Item
            Implicits: 1
            Flasks gain a Charge every 3 seconds
            +90 to maximum Life
            """)!;
        Assert.Equal(Influence.Exarch, item.Mods[0].Influence);
        Assert.Null(item.Mods[1].Influence);
    }

    [Fact]
    public void PobDefencesReachThePriceCheck()
    {
        var item = PobItems.ParseItem(1, """
            Rarity: RARE
            Victory Barrier
            Crusader Buckler
            Evasion: 812
            EvasionBasePercentile: 0.5
            Item Level: 84
            Quality: 20
            Implicits: 1
            9% increased Movement Speed
            +194 to Evasion Rating
            """)!;
        Assert.Equal(812, item.Evasion);
        var game = GameItem.Parse(item.ToGameText("Shields"))!;
        Assert.Equal(812, game.Evasion);
        Assert.Equal(20, game.Quality);
        Assert.Null(game.Armour);
    }
}
