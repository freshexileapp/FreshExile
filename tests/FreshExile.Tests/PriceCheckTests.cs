using System.Text.Json;
using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class PriceCheckTests
{
    // Ctrl+Alt+C on rare boots: tiers, ranges, a crafted line, corrupted
    const string Advanced = """
        Item Class: Boots
        Rarity: Rare
        Ghoul Stride
        Hydrascale Boots
        --------
        Quality: +10% (augmented)
        Armour: 132 (augmented)
        Evasion Rating: 132 (augmented)
        --------
        Requirements:
        Level: 59
        Str: 56
        Dex: 56
        --------
        Sockets: R-R-G B
        --------
        Item Level: 75
        --------
        { Prefix Modifier "Agile" (Tier: 3) — Attribute }
        +40(38-42) to Dexterity
        { Suffix Modifier "of the Cheetah" (Tier: 1) — Speed }
        30(30-30)% increased Movement Speed
        { Suffix Modifier "of Ephij" (Tier: 7) — Elemental, Lightning, Resistance }
        +34(30-35)% to Lightning Resistance
        { Suffix Modifier "of the Kiln" (Tier: 7) — Elemental, Fire, Resistance }
        +37(36-41)% to Fire Resistance
        { Prefix Modifier "Hale" (Tier: 6) — Life }
        +45(40-49) to maximum Life
        { Master Crafted Suffix Modifier "of Craft" (Rank: 1) — Elemental, Cold, Resistance }
        +4(4-5)% to all Elemental Resistances (crafted)
        --------
        Corrupted
        """;

    // plain Ctrl+C on a unique: flavour text after the mods must not become a mod
    const string PlainUnique = """
        Item Class: Belts
        Rarity: Unique
        Headhunter
        Leather Belt
        --------
        Requirements:
        Level: 40
        --------
        Item Level: 84
        --------
        +32 to maximum Life (implicit)
        --------
        +44 to Strength
        +50 to Dexterity
        +60 to maximum Life
        24% increased Damage with Hits against Rare monsters
        When you Kill a Rare monster, you gain its Modifiers for 60 seconds
        --------
        "A man's soul rules from a cavern of bone, learns and
        judges through flesh-born windows."
        """;

    const string Stats = """
        {"result":[
          {"id":"pseudo","entries":[
            {"id":"pseudo.pseudo_total_elemental_resistance","text":"+#% total Elemental Resistance"},
            {"id":"pseudo.pseudo_total_life","text":"+# total maximum Life"}]},
          {"id":"explicit","entries":[
            {"id":"explicit.stat_dex","text":"+# to Dexterity"},
            {"id":"explicit.stat_ms","text":"#% increased Movement Speed"},
            {"id":"explicit.stat_light","text":"+#% to Lightning Resistance"},
            {"id":"explicit.stat_fire","text":"+#% to Fire Resistance"},
            {"id":"explicit.stat_life","text":"+# to maximum Life"},
            {"id":"explicit.stat_allres","text":"+#% to all Elemental Resistances"},
            {"id":"explicit.stat_allattr","text":"+# to all Attributes"}]},
          {"id":"crafted","entries":[
            {"id":"crafted.stat_allres","text":"+#% to all Elemental Resistances"}]}
        ]}
        """;

    [Fact]
    public void Advanced_copy_reads_tiers_ranges_and_properties()
    {
        var item = GameItem.Parse(Advanced)!;
        Assert.Equal("Boots", item.ItemClass);
        Assert.Equal(ItemRarity.Rare, item.Rarity);
        Assert.Equal("Ghoul Stride", item.Name);
        Assert.Equal("Hydrascale Boots", item.BaseType);
        Assert.Equal(75, item.ItemLevel);
        Assert.Equal(10, item.Quality);
        Assert.Equal(132, item.Armour);
        Assert.Equal(3, item.Links);
        Assert.True(item.Corrupted);

        Assert.Equal(6, item.Mods.Count);
        var dex = item.Mods[0];
        Assert.Equal("+40 to Dexterity", dex.Text);
        Assert.Equal(3, dex.Tier);
        Assert.Equal("38-42", dex.Range);
        Assert.Equal(40, dex.Value);
        Assert.Equal(ModKind.Crafted, item.Mods[5].Kind);
        Assert.Equal("+4% to all Elemental Resistances", item.Mods[5].Text);
    }

    [Fact]
    public void Plain_copy_of_a_unique_keeps_mods_and_drops_flavour()
    {
        var item = GameItem.Parse(PlainUnique)!;
        Assert.Equal("Headhunter", item.Name);
        Assert.Equal(ItemRarity.Unique, item.Rarity);
        Assert.Equal(ModKind.Implicit, item.Mods[0].Kind);
        Assert.Equal("+32 to maximum Life", item.Mods[0].Text);
        Assert.Equal(6, item.Mods.Count);
        Assert.DoesNotContain(item.Mods, m => m.Text.Contains("soul", StringComparison.Ordinal));
    }

    [Fact]
    public void Not_an_item_is_null()
    {
        Assert.Null(GameItem.Parse("hello"));
        Assert.Null(GameItem.Parse(""));
    }

    [Fact]
    public void Totals_count_two_element_and_all_res_lines_and_half_strength()
    {
        var item = GameItem.Parse(Advanced)!;
        Assert.Equal(34 + 37 + 4 * 3, item.TotalElementalResistance);
        Assert.Equal(45, item.TotalLife);
        Assert.Equal(60 + 32 + 44 / 2, GameItem.Parse(PlainUnique)!.TotalLife);
    }

    [Fact]
    public void Defences_are_scaled_to_full_quality_unless_corrupted()
    {
        var corrupted = GameItem.Parse(Advanced)!;
        Assert.Equal(132, corrupted.AtFullQuality(corrupted.Armour, "Armour"));

        var clean = GameItem.Parse(Advanced.Replace("Corrupted", ""))!;
        // 132 at 10% → at 20%: 132 * 120 / 110
        Assert.Equal(144, clean.AtFullQuality(clean.Armour, "Armour"));
    }

    [Fact]
    public void Default_ticks_take_totals_and_valuable_lines_not_the_lines_inside_totals()
    {
        var trade = TradeLink.FromData("Covens", Stats);
        var check = PriceCheck.For(GameItem.Parse(Advanced)!, trade, 90);
        var on = check.Filters.Where(f => f.Enabled).Select(f => f.StatId ?? f.Kind.ToString()).ToList();

        Assert.Contains("pseudo.pseudo_total_elemental_resistance", on);
        Assert.Contains("pseudo.pseudo_total_life", on);
        Assert.Contains("explicit.stat_ms", on);           // T1
        Assert.DoesNotContain("explicit.stat_dex", on);    // T3: only T1-T2 are ticked
        Assert.DoesNotContain("Armour", on);               // armour + evasion: a hybrid base is priced by its mods
        Assert.DoesNotContain("explicit.stat_fire", on);   // inside the total
        Assert.DoesNotContain("explicit.stat_life", on);
        Assert.Equal(Math.Floor(83 * 0.9), check.Filters.First(f => f.IsPseudo && f.Value == 83).Min);
    }

    [Fact]
    public void Query_has_category_totals_defences_and_trade_options()
    {
        var trade = TradeLink.FromData("Covens", Stats);
        var check = PriceCheck.For(GameItem.Parse(Advanced)!, trade, 90);
        check.Filters.First(x => x.Kind == PriceFilterKind.Armour).Enabled = true;
        var q = check.Query(trade, new PriceSettings { Currency = "chaos_divine", Listed = "1week" }).ToJsonString();
        using var doc = JsonDocument.Parse(q);
        var query = doc.RootElement.GetProperty("query");
        var f = query.GetProperty("filters");

        Assert.Equal("armour.boots", f.GetProperty("type_filters").GetProperty("filters").GetProperty("category").GetProperty("option").GetString());
        Assert.False(query.TryGetProperty("type", out _));
        Assert.Equal("true", f.GetProperty("misc_filters").GetProperty("filters").GetProperty("corrupted").GetProperty("option").GetString());
        Assert.Equal(118, f.GetProperty("armour_filters").GetProperty("filters").GetProperty("ar").GetProperty("min").GetDouble()); // floor(132 * .9)
        Assert.Equal("chaos_divine", f.GetProperty("trade_filters").GetProperty("filters").GetProperty("price").GetProperty("option").GetString());
        Assert.Equal("1week", f.GetProperty("trade_filters").GetProperty("filters").GetProperty("indexed").GetProperty("option").GetString());
        var stats = query.GetProperty("stats")[0].GetProperty("filters").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains("pseudo.pseudo_total_elemental_resistance", stats);

        check.UseBase = true;
        var withBase = JsonDocument.Parse(check.Query(trade, new PriceSettings()).ToJsonString()).RootElement.GetProperty("query");
        Assert.Equal("Hydrascale Boots", withBase.GetProperty("type").GetString());
    }

    [Fact]
    public void Unique_is_searched_by_name_without_ticks()
    {
        var trade = TradeLink.FromData("Covens", Stats);
        var check = PriceCheck.For(GameItem.Parse(PlainUnique)!, trade, 90);
        Assert.DoesNotContain(check.Filters, f => f.Enabled);
        var q = JsonDocument.Parse(check.Query(trade, new PriceSettings()).ToJsonString()).RootElement.GetProperty("query");
        Assert.Equal("Headhunter", q.GetProperty("name").GetString());
        Assert.Equal("Leather Belt", q.GetProperty("type").GetString());
    }

    [Fact]
    public void Rate_gate_keeps_one_in_reserve_and_waits_for_the_window()
    {
        var gate = new RateGate((5, 10));
        var t0 = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(TimeSpan.Zero, gate.WaitBefore(t0.AddSeconds(i)));
            gate.Sent(t0.AddSeconds(i));
        }
        // four sent in 10 s out of five allowed: the fifth waits until the first leaves the window
        Assert.Equal(TimeSpan.FromSeconds(6), gate.WaitBefore(t0.AddSeconds(4)));
        Assert.Equal(TimeSpan.Zero, gate.WaitBefore(t0.AddSeconds(10.5)));
    }

    [Fact]
    public void Rate_gate_obeys_the_sites_state_and_ban()
    {
        var gate = new RateGate((5, 10));
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var rules = RateGate.ParseRules("5:10:60,15:60:300").Select(r => (r.A, r.B)).ToList();
        // another tool already spent 14 of 15 per minute
        gate.Apply(rules, [(1, 10, 0), (14, 60, 0)], null, now);
        Assert.Equal(TimeSpan.FromSeconds(60), gate.WaitBefore(now));

        var banned = new RateGate((5, 10));
        banned.Apply(rules, [(6, 10, 60)], null, now);
        Assert.Equal(TimeSpan.FromSeconds(60), banned.WaitBefore(now));
    }

    [Fact]
    public void Fetch_answer_becomes_listings()
    {
        const string json = """
            {"result":[{"id":"a1","listing":{"indexed":"2026-11-16T10:00:00Z","account":{"name":"seller#1234"},
              "price":{"type":"~price","amount":1,"currency":"divine"}},
              "item":{"ilvl":75,"corrupted":true,"sockets":[{"group":0,"sColour":"R"},{"group":0,"sColour":"G"},{"group":1,"sColour":"B"}]}}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var l = Assert.Single(TradeApi.ReadListings(doc.RootElement));
        Assert.Equal(1, l.Amount);
        Assert.Equal("divine", l.Currency);
        Assert.Equal("seller#1234", l.Account);
        Assert.Equal("R-G B", l.Sockets);
        Assert.True(l.Corrupted);
        Assert.Equal(75, l.ItemLevel);
    }

    [Fact]
    public void Offer_is_compared_with_our_item_line_by_line()
    {
        const string json = """
            {"result":[{"id":"a1","listing":{"account":{"name":"s"},"price":{"amount":1,"currency":"divine"}},
              "item":{"ilvl":80,
                "properties":[{"name":"Quality","values":[["+20%",1]]},{"name":"Armour","values":[["100",1]]}],
                "explicitMods":["+45 to Dexterity","25% increased Movement Speed","+90 to maximum Life","+20% to Cold Resistance","Cannot be Frozen"],
                "extended":{"mods":{"explicit":[{"tier":"S1"},{"tier":"P4"},{"tier":"P3"}]},
                  "hashes":{"explicit":[["explicit.stat_dex",[2]],["explicit.stat_ms",[0]],["explicit.stat_life",[1]]]}}}}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var offer = Assert.Single(TradeApi.ReadListings(doc.RootElement));
        Assert.Equal(45, offer.Stats["explicit.stat_dex"]);
        Assert.Equal(100, offer.Item!.Armour);

        var check = PriceCheck.For(GameItem.Parse(Advanced)!, TradeLink.FromData("Covens", Stats), 90);
        var rows = PriceCompare.Compare(check, offer);
        CompareRow Row(string start) => rows.First(r => r.Label.StartsWith(start, StringComparison.Ordinal));

        Assert.Equal(("40", "45", -1), (Row("+# to Dex").Ours, Row("+# to Dex").Theirs, Row("+# to Dex").Better));
        Assert.Equal(("30", "25", 1), (Row("#% increased Movement").Ours, Row("#% increased Movement").Theirs, Row("#% increased Movement").Better));
        // tiers: ours from the advanced copy, theirs from extended.mods through the line's hash entry
        Assert.Equal((3, 3), (Row("+# to Dex").OursTier, Row("+# to Dex").TheirsTier));
        Assert.Equal((1, 1), (Row("#% increased Movement").OursTier, Row("#% increased Movement").TheirsTier));
        Assert.True(Row("#% increased Movement").Top);
        Assert.False(Row("+# to Dex").Top);
        Assert.True(Row("Cannot be Frozen").Top);
        Assert.Equal(("83", "20", 1), (Row("Сумма сопротивлений").Ours, Row("Сумма сопротивлений").Theirs, Row("Сумма сопротивлений").Better));
        Assert.Equal(("45", "90", -1), (Row("Всего здоровья").Ours, Row("Всего здоровья").Theirs, Row("Всего здоровья").Better));
        Assert.Equal(("132", "100", 1), (Row("Броня").Ours, Row("Броня").Theirs, Row("Броня").Better));
        // their lines we lack: theirs only
        Assert.Equal(("—", "20", -1), (Row("+20% to Cold").Ours, Row("+20% to Cold").Theirs, Row("+20% to Cold").Better));
        Assert.Equal("есть", Row("Cannot be Frozen").Theirs);
        // a line of ours they lack
        Assert.Equal("—", Row("+#% to Lightning").Theirs);
    }

    [Fact]
    public void Top_mods_go_by_slot_and_tier()
    {
        var top = TopMods.Default;
        // movement speed sells on boots, not on a ring; a low tier is not top, an unknown one goes by the slot alone
        Assert.True(top.IsTop("30% increased Movement Speed", "armour.boots", 1));
        Assert.False(top.IsTop("30% increased Movement Speed", "accessory.ring", 1));
        Assert.False(top.IsTop("+45 to maximum Life", "armour.boots", 6));
        Assert.True(top.IsTop("+45 to maximum Life", "armour.boots", null));
        // "always": any slot, any tier
        Assert.True(top.IsTop("+1 to Maximum Frenzy Charges", "armour.gloves", null));
        Assert.True(top.IsTop("+2 to Level of Socketed Duration Gems", "armour.gloves", 5));
        // "#" is a number
        Assert.True(top.IsTop("Adds 5 to 10 Physical Damage to Attacks", "armour.gloves", 2));

        // the player's own list replaces the slot's default; tier "any" lets a T6 through
        var mine = new TopMods(new Dictionary<string, List<string>> { ["boots"] = ["to Dexterity"] }, share: 0);
        Assert.True(mine.IsTop("+40 to Dexterity", "armour.boots", 6));
        Assert.False(mine.IsTop("30% increased Movement Speed", "armour.boots", 1));
    }

    [Fact]
    public void Top_is_the_upper_third_of_the_mods_own_tiers()
    {
        // life on gloves / boots: Hale … Athlete's = 9 tiers (Fecund is body armour and shields only)
        Assert.Equal(9, ModTiers.Count("Slink Gloves", "Hale", prefix: true));
        Assert.Equal(9, ModTiers.Count("Hydrascale Boots", "Virile", prefix: null));
        Assert.Null(ModTiers.Count("Hydrascale Boots", "Hale", prefix: false)); // life is never a suffix
        Assert.Null(ModTiers.Count("No Such Base", "Hale", prefix: true));

        var top = TopMods.Default;
        Assert.Equal(3, top.Limit(9));   // a third of 9: T1-T3
        Assert.Equal(1, top.Limit(2));   // a mod with 2 tiers: T1 only
        Assert.Equal(2, top.Limit(null)); // unknown: as 6
        Assert.True(top.IsTop("+80 to maximum Life", "armour.gloves", 3, 9));
        Assert.False(top.IsTop("+43 to maximum Life", "armour.gloves", 6, 9));

        // the advanced copy names the affix
        var hale = GameItem.Parse(Advanced)!.Mods.First(m => m.Text.EndsWith("maximum Life", StringComparison.Ordinal));
        Assert.Equal(("Hale", true, 6), (hale.Name, hale.Prefix, hale.Tier));
    }

    [Fact]
    public void A_fractured_line_described_in_place_keeps_every_line_on_its_own_stat()
    {
        // the site's answer of 2026-11-19: mods as objects with their own hash and tier; the fractured one sits among the
        // explicit ones but hashes.explicit leaves it out — counting by index gave each line the next one's stat
        const string item = """
            {"baseType":"Murderous Eye Jewel","ilvl":85,"explicitMods":[
              {"description":"4 to 6 Added Physical Damage with Dagger Attacks","flags":{"fractured":true},"domain":"fractured","hash":"stat.fractured.stat_1298238534","mods":[{"name":"Tempered","tier":"P3"}]},
              {"description":"Adds 1 to 29 Lightning Damage to Attacks","domain":"explicit","hash":"stat.explicit.stat_1754445556","mods":[{"name":"of Voltage","tier":"S3"}]},
              {"description":"+11% to Fire and Lightning Resistances","domain":"explicit","hash":"stat.explicit.stat_3441501978","mods":[{"name":"of Insulation","tier":"S1"}]}],
             "extended":{"hashes":{"explicit":[["explicit.stat_1754445556",[0]],["explicit.stat_3441501978",[1]]],"fractured":[["fractured.stat_1298238534",[0]]]}}}
            """;
        var offer = TradeApi.ReadOffer(item, 85, false, null);
        Assert.Equal(3, offer.Lines.Count);
        var frac = offer.Lines[0];
        Assert.Equal((ModKind.Fractured, "fractured.stat_1298238534", (int?)3, (bool?)true), (frac.Kind, frac.StatId, frac.Tier, frac.Prefix));
        Assert.Equal(("explicit.stat_3441501978", (int?)1), (offer.Lines[2].StatId, offer.Lines[2].Tier));
        Assert.Equal(11, offer.Stats["explicit.stat_3441501978"]);
        Assert.Equal(ModKind.Fractured, offer.Item!.Mods.First(m => m.Text.StartsWith("4 to 6")).Kind);
    }

    [Fact]
    public void A_stacks_price_is_per_piece_and_its_size_is_the_stock()
    {
        // "10 divine" on a stack of 6 Eldritch Exalted Orbs is 10 divine each (the exchange lists the same offer as 10:1, stock 6)
        const string json = """
            {"result":[{"id":"s1","listing":{"account":{"name":"s"},"price":{"amount":10,"currency":"divine"}},
              "item":{"ilvl":0,"stackSize":6,"maxStackSize":20,"typeLine":"Eldritch Exalted Orb"}},
             {"id":"s2","listing":{"account":{"name":"t"},"price":{"amount":3,"currency":"chaos"}},"item":{"ilvl":80}}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var l = TradeApi.ReadListings(doc.RootElement);
        Assert.Equal((10.0, 6, true), (l[0].Amount, l[0].Stock, l[0].Stackable));
        Assert.Equal((3.0, 1, false), (l[1].Amount, l[1].Stock, l[1].Stackable));
    }

    [Fact]
    public void The_bulk_exchange_gives_price_per_piece_stock_and_the_sellers_ratio()
    {
        // the site's answer of 2026-11-19 (accounts shortened)
        const string json = """
            {"id":"Q1","complexity":null,"total":3,"result":{
              "a":{"id":"a","item":null,"listing":{"indexed":"2026-11-19T07:25:27+00:00","account":{"name":"One#1"},
                "offers":[{"exchange":{"currency":"divine","amount":1},"item":{"currency":"eldritch-exalted-orb","amount":1,"stock":5}}]}},
              "b":{"id":"b","item":null,"listing":{"indexed":"2026-11-19T06:37:05+00:00","account":{"name":"Two#2"},
                "offers":[{"exchange":{"currency":"divine","amount":10},"item":{"currency":"eldritch-exalted-orb","amount":1,"stock":6}}]}},
              "c":{"id":"c","item":null,"listing":{"account":{"name":"Three#3"},
                "offers":[{"exchange":{"currency":"chaos","amount":150},"item":{"currency":"eldritch-exalted-orb","amount":4,"stock":40}}]}}}}
            """;
        using var doc = JsonDocument.Parse(json);
        var r = TradeApi.ReadExchange(doc.RootElement);
        Assert.Equal(("Q1", 3, 3), (r.Id, r.Total, r.Offers.Count));
        Assert.Equal((1.0, "divine", 5, (ValueTuple<double, double>?)null), (r.Offers[0].Amount, r.Offers[0].Currency, r.Offers[0].Stock, r.Offers[0].Ratio));
        Assert.Equal((10.0, 6), (r.Offers[1].Amount, r.Offers[1].Stock));
        Assert.Equal((37.5, "chaos", 40, (ValueTuple<double, double>?)(4.0, 150.0)), (r.Offers[2].Amount, r.Offers[2].Currency, r.Offers[2].Stock, r.Offers[2].Ratio));
    }

    [Fact]
    public void Currency_is_found_on_the_exchange_by_its_name()
    {
        const string staticJson = """
            {"result":[{"id":"Currency","label":"Currency","entries":[{"id":"chaos","text":"Chaos Orb"},{"id":"eldritch-exalted-orb","text":"Eldritch Exalted Orb"}]},
                       {"id":"Cards","entries":[{"id":"the-doctor","text":"The Doctor"}]}]}
            """;
        var link = TradeLink.FromData("Covens", null, null, staticJson);
        Assert.Equal("eldritch-exalted-orb", link.ExchangeId(new GameItem { BaseType = "Eldritch Exalted Orb" }));
        Assert.Equal("the-doctor", link.ExchangeId(new GameItem { BaseType = "The Doctor" }));
        Assert.Null(link.ExchangeId(new GameItem { BaseType = "Murderous Eye Jewel", Name = "Foul Scalpel" }));
    }

    [Fact]
    public void Odd_entries_in_the_item_never_cost_the_offer()
    {
        // an object where a mod line was expected (the site's answer of 2026-11-18): the offer still comes, indexes stay in step
        const string json = """
            {"result":[{"id":"a1","listing":{"account":{"name":"s"},"price":{"amount":2,"currency":"chaos"}},
              "item":{"ilvl":80,"properties":[{"name":{"x":1},"values":[[{"x":1},1]]}],
                "explicitMods":[{"x":1},"+45 to Dexterity"],
                "extended":{"hashes":{"explicit":[[{"x":1},[0]],["explicit.stat_dex",[1]]]}}}}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var offer = Assert.Single(TradeApi.ReadListings(doc.RootElement));
        Assert.Equal(2, offer.Amount);
        Assert.Equal(45, offer.Stats["explicit.stat_dex"]);
    }

    [Fact]
    public void Ninja_divine_rate_is_read()
    {
        const string json = """{"lines":[{"currencyTypeName":"Mirror of Kalandra","chaosEquivalent":90000},{"currencyTypeName":"Divine Orb","chaosEquivalent":115.0}]}""";
        Assert.Equal(115, NinjaRates.ReadDivine(json));
    }

    [Fact]
    public void Requirement_level_is_read_and_the_wear_now_filter_caps_it()
    {
        var item = GameItem.Parse(Advanced)!;
        Assert.Equal(59, item.RequiredLevel);

        var trade = TradeLink.FromData("Covens", Stats);
        var check = PriceCheck.For(item, trade, 90, characterLevel: 38);
        Assert.True(check.UseLevelCap);
        var q = JsonDocument.Parse(check.Query(trade, new PriceSettings()).ToJsonString()).RootElement.GetProperty("query");
        Assert.Equal(38, q.GetProperty("filters").GetProperty("req_filters").GetProperty("filters").GetProperty("lvl").GetProperty("max").GetInt32());

        // unknown level or end game: no cap
        Assert.False(PriceCheck.For(item, trade, 90).UseLevelCap);
        Assert.False(PriceCheck.For(item, trade, 90, characterLevel: 95).UseLevelCap);
    }

    [Fact]
    public void Mana_total_counts_half_the_intelligence()
    {
        var item = GameItem.Parse(Advanced.Replace("+40(38-42) to Dexterity", "+30(28-32) to Intelligence")
                                          .Replace("+45(40-49) to maximum Life", "+49(40-49) to maximum Mana"))!;
        Assert.Equal(49 + 15, item.TotalMana);
    }

    [Fact]
    public void Ninja_price_takes_the_line_with_the_items_links()
    {
        const string json = """
            {"lines":[
              {"name":"Dendrobate","baseType":"Sentinel Jacket","links":6,"chaosValue":280.0,"divineValue":1.89},
              {"name":"Dendrobate","baseType":"Sentinel Jacket","links":5,"chaosValue":44.6,"divineValue":0.3},
              {"name":"Dendrobate","baseType":"Sentinel Jacket","chaosValue":4.0,"divineValue":0.03}]}
            """;
        Assert.Equal(44.6, NinjaUniques.Find(json, "Dendrobate", "Sentinel Jacket", 5)!.Chaos);
        Assert.Equal(4.0, NinjaUniques.Find(json, "Dendrobate", "Sentinel Jacket", 4)!.Chaos);
        Assert.Null(NinjaUniques.Find(json, "Kaom's Heart", "Glorious Plate", 0));
        Assert.Equal("UniqueArmour", NinjaUniques.KindOf("Body Armours"));
        Assert.Equal("UniqueWeapon", NinjaUniques.KindOf("Bows"));
    }

    [Fact]
    public void Item_flags_become_three_state_misc_filters()
    {
        var trade = TradeLink.FromData("Covens", Stats);
        // corrupted rare: only corrupted ones, mirrored copies left out
        var rare = PriceCheck.For(GameItem.Parse(Advanced)!, trade, 90);
        var misc = JsonDocument.Parse(rare.Query(trade, new PriceSettings()).ToJsonString()).RootElement
            .GetProperty("query").GetProperty("filters").GetProperty("misc_filters").GetProperty("filters");
        Assert.Equal("true", misc.GetProperty("corrupted").GetProperty("option").GetString());
        Assert.Equal("false", misc.GetProperty("mirrored").GetProperty("option").GetString());
        Assert.False(misc.TryGetProperty("identified", out _));

        // clean unique: not corrupted, not Foulborn; "any" drops the filter
        var unique = PriceCheck.For(GameItem.Parse(PlainUnique)!, trade, 90);
        unique.Mirrored = null;
        var um = JsonDocument.Parse(unique.Query(trade, new PriceSettings()).ToJsonString()).RootElement
            .GetProperty("query").GetProperty("filters").GetProperty("misc_filters").GetProperty("filters");
        Assert.Equal("false", um.GetProperty("corrupted").GetProperty("option").GetString());
        Assert.Equal("false", um.GetProperty("mutated").GetProperty("option").GetString());
        Assert.False(um.TryGetProperty("mirrored", out _));
    }

    [Fact]
    public void League_item_without_a_rarity_line_is_searched_by_its_name()
    {
        // a Wickerman eye as the game copies it (Ctrl+Alt+C): Item Class, then the name, no Rarity line
        const string eye = """
            Item Class: Viscera
            Greater Engorged Eye
            --------
            Head
            --------
            Item Level: 72
            --------
            { Implicit Modifier }
            Corrupted Blood cannot be inflicted on you — Unscalable Value
            --------
            Take this to the Wickerman in a town or hideout to attach it.
            """;
        var item = GameItem.Parse(eye)!;
        Assert.Equal("Greater Engorged Eye", item.BaseType);
        Assert.Equal(72, item.ItemLevel);
        Assert.Equal("Corrupted Blood cannot be inflicted on you", Assert.Single(item.Mods).Text);

        // the site lists the base as "Engorged Eye": "Greater" is not part of the type
        const string items = """{"result":[{"id":"viscera","label":"Viscera","entries":[{"type":"Engorged Eye","text":"Engorged Eye"}]}]}""";
        var trade = TradeLink.FromData("Covens", Stats, items);
        var q = JsonDocument.Parse(PriceCheck.For(item, trade, 90).Query(trade, new PriceSettings()).ToJsonString()).RootElement.GetProperty("query");
        Assert.Equal("Engorged Eye", q.GetProperty("type").GetString());
    }

    [Fact]
    public void A_magic_jewel_is_compared_with_magic_ones()
    {
        const string jewel = """
            Item Class: Jewels
            Rarity: Magic
            Vivid Viridian Jewel of Potency
            --------
            Item Level: 82
            --------
            5% increased maximum Life
            +9% to Global Critical Strike Multiplier
            """;
        const string items = """{"result":[{"id":"jewel","label":"Jewels","entries":[{"type":"Viridian Jewel"}]}]}""";
        var trade = TradeLink.FromData("Covens", Stats, items);
        var check = PriceCheck.For(GameItem.Parse(jewel)!, trade, 90);
        Assert.True(check.UseRarity);
        var f = JsonDocument.Parse(check.Query(trade, new PriceSettings()).ToJsonString()).RootElement.GetProperty("query").GetProperty("filters");
        Assert.Equal("magic", f.GetProperty("type_filters").GetProperty("filters").GetProperty("rarity").GetProperty("option").GetString());
    }

    [Fact]
    public void A_heist_contract_is_searched_by_its_job_level_and_area_level()
    {
        const string contract = """
            Item Class: Contracts
            Rarity: Rare
            Sol Pledge
            Contract: Bunker
            --------
            Client: Kondor Larcius
            Heist Target: Bust of Emperor Caspiro (High Value)
            Area Level: 80
            Requires Agility (Level 1)
            --------
            Item Rarity: +90% (augmented)
            Alert Level Reduction: +29% (augmented)
            --------
            Item Level: 81
            --------
            Players are Cursed with Enfeeble
            Monsters are Hexproof
            --------
            "Caspiro was a master of presentation."
            --------
            Give this Contract to Adiyah in the Rogue Harbour to embark on the Heist.
            """;
        const string items = """{"result":[{"id":"heistmission","label":"Heist","entries":[{"type":"Contract: Bunker"}]}]}""";
        var trade = TradeLink.FromData("Covens", Stats, items);
        var item = GameItem.Parse(contract)!;
        Assert.Equal(80, item.AreaLevel);
        Assert.Equal("Agility", item.HeistJob);
        Assert.Equal(1, item.HeistJobLevel);

        var q = JsonDocument.Parse(PriceCheck.For(item, trade, 90).Query(trade, new PriceSettings()).ToJsonString()).RootElement.GetProperty("query");
        Assert.Equal("Contract: Bunker", q.GetProperty("type").GetString());
        var f = q.GetProperty("filters");
        Assert.Equal(80, f.GetProperty("map_filters").GetProperty("filters").GetProperty("area_level").GetProperty("min").GetInt32());
        Assert.Equal(1, f.GetProperty("heist_filters").GetProperty("filters").GetProperty("heist_agility").GetProperty("min").GetInt32());
    }

    [Fact]
    public void A_magic_flask_is_searched_by_its_base_read_out_of_the_name()
    {
        const string flask = """
            Item Class: Utility Flasks
            Rarity: Magic
            Allocated Quicksilver Flask of Bracing
            --------
            Item Level: 83
            --------
            58% increased Charge Recovery
            62% increased Block and Stun Recovery during Effect
            """;
        const string items = """{"result":[{"id":"flask","label":"Flasks","entries":[{"type":"Quicksilver Flask"},{"type":"Granite Flask"}]}]}""";
        var trade = TradeLink.FromData("Covens", Stats, items);
        var check = PriceCheck.For(GameItem.Parse(flask)!, trade, 90);
        Assert.Equal("Quicksilver Flask", check.Item.BaseType);
        Assert.True(check.UseBase);
        var q = JsonDocument.Parse(check.Query(trade, new PriceSettings()).ToJsonString()).RootElement.GetProperty("query");
        Assert.Equal("Quicksilver Flask", q.GetProperty("type").GetString());
    }

    [Fact]
    public void A_line_that_alone_can_make_the_price_is_flagged_and_kept_in_view()
    {
        // the eye that went for 1c without its implicit and for 130c with it
        const string eye = """
            Item Class: Viscera
            Greater Engorged Eye
            --------
            Item Level: 72
            --------
            { Implicit Modifier }
            Corrupted Blood cannot be inflicted on you — Unscalable Value
            """;
        const string stats = """{"result":[{"id":"implicit","entries":[{"id":"implicit.stat_1658498488","text":"Corrupted Blood cannot be inflicted on you"}]}]}""";
        var trade = TradeLink.FromData("Covens", stats, """{"result":[]}""");
        var f = Assert.Single(PriceCheck.For(GameItem.Parse(eye)!, trade, 90).Filters);
        Assert.False(f.Enabled);
        Assert.NotNull(f.Hint);
        Assert.True(f.Shown);
    }

    [Fact]
    public void Unique_rolled_lines_are_ticked_with_a_minimum_off_the_range()
    {
        const string unique = """
            Item Class: Rings
            Rarity: Unique
            Le Heup of All
            Iron Ring
            --------
            Item Level: 80
            --------
            { Unique Modifier }
            +28(10-30) to all Attributes
            { Unique Modifier }
            +20% to all Elemental Resistances
            """;
        var trade = TradeLink.FromData("Covens", Stats);
        var check = PriceCheck.For(GameItem.Parse(unique)!, trade, 90);
        var attrs = check.Filters.First(f => f.Label.Contains("Attributes"));
        Assert.True(attrs.Enabled);                         // rolled, and one of at most three
        Assert.Equal(26, attrs.Min);                        // 28 - 10% of (30 - 10)
        Assert.False(check.Filters.First(f => f.Label.StartsWith("+20%")).Shown); // fixed line: under the fold
        // one resistance line on a unique is not a total
        Assert.DoesNotContain(check.Filters, f => f.StatId == "pseudo.pseudo_total_elemental_resistance");
    }

    const string ClusterStats = """
        {"result":[
          {"id":"explicit","entries":[
            {"id":"explicit.stat_ue","text":"1 Added Passive Skill is Unwaveringly Evil"},
            {"id":"explicit.stat_ug","text":"1 Added Passive Skill is Unholy Grace"},
            {"id":"explicit.stat_wp","text":"1 Added Passive Skill is Wicked Pall"},
            {"id":"explicit.stat_di","text":"1 Added Passive Skill is Dark Ideation"},
            {"id":"explicit.stat_go","text":"1 Added Passive Skill is Grim Oath"},
            {"id":"explicit.stat_fire","text":"Added Small Passive Skills also grant: +#% to Fire Resistance"}]},
          {"id":"enchant","entries":[
            {"id":"enchant.stat_adds","text":"Adds # Passive Skills"},
            {"id":"enchant.stat_sockets","text":"# Added Passive Skills are Jewel Sockets"},
            {"id":"enchant.stat_3948993189|16","text":"Added Small Passive Skills grant: 12% increased Chaos Damage"}]}]}
        """;

    // as the gear tab hands a PoB cluster to the price check: the base lines are enchants
    const string ChaosCluster = """
        Item Class: Cluster Jewels
        Rarity: Rare
        Luminous Spark
        Large Cluster Jewel
        --------
        Item Level: 83
        --------
        Adds 8 Passive Skills (enchant)
        2 Added Passive Skills are Jewel Sockets (enchant)
        Added Small Passive Skills grant: 12% increased Chaos Damage (enchant)
        --------
        Added Small Passive Skills also grant: +5% to Fire Resistance
        1 Added Passive Skill is Unholy Grace
        1 Added Passive Skill is Unwaveringly Evil
        1 Added Passive Skill is Wicked Pall
        """;

    [Fact]
    public void Cluster_search_wants_the_builds_notables_and_any_third_that_keeps_off_their_path()
    {
        var trade = TradeLink.FromData("Covens", ClusterStats);
        var check = PriceCheck.For(GameItem.Parse(ChaosCluster)!, trade, 90, wantedNotables: ["Unwaveringly Evil", "Wicked Pall"]);
        var on = check.Filters.Where(f => f.Enabled).Select(f => f.StatId).ToList();

        Assert.Equal(["enchant.stat_adds", "enchant.stat_sockets", "enchant.stat_3948993189|16", "explicit.stat_ue", "explicit.stat_wp"], on);
        Assert.Equal(8, check.Filters.First(f => f.StatId == "enchant.stat_adds").Max);   // exactly 8: the ring decides the places
        Assert.True(check.Filters.First(f => f.StatId == "explicit.stat_ug").Shown);     // the unwanted one stays in view, unticked
        Assert.True(check.UseClusterLayout);

        using var doc = JsonDocument.Parse(check.Query(trade, new PriceSettings()).ToJsonString());
        var groups = doc.RootElement.GetProperty("query").GetProperty("stats").EnumerateArray().ToList();
        var count = groups.Single(g => g.GetProperty("type").GetString() == "count");
        Assert.Equal(1, count.GetProperty("value").GetProperty("min").GetInt32());
        // Unholy Grace and Dark Ideation sort between the two wanted ones (Unspeakable Gifts too, not in this stat list);
        // Grim Oath sorts first and would take the place by the entry
        Assert.Equal(["explicit.stat_di", "explicit.stat_ug"],
            count.GetProperty("filters").EnumerateArray().Select(f => f.GetProperty("id").GetString()).Order().ToList());

        check.UseClusterLayout = false;
        using var plain = JsonDocument.Parse(check.Query(trade, new PriceSettings()).ToJsonString());
        Assert.Single(plain.RootElement.GetProperty("query").GetProperty("stats").EnumerateArray());
    }

    // a medium cluster: its one socket reads "1 Added Passive Skill is a Jewel Socket", like a notable
    const string ProjectileMedium = """
        Item Class: Cluster Jewels
        Rarity: Rare
        Foe Glimmer
        Medium Cluster Jewel
        --------
        Item Level: 70
        --------
        Adds 4 Passive Skills (implicit)
        1 Added Passive Skill is a Jewel Socket (implicit)
        Added Small Passive Skills grant: 10% increased Projectile Damage (implicit)
        --------
        Added Small Passive Skills also grant: +4% to Chaos Resistance
        1 Added Passive Skill is Aerodynamics
        1 Added Passive Skill is Follow-Through
        """;

    [Fact]
    public void A_medium_clusters_socket_is_no_notable_and_is_searched_by_the_sites_plural_line()
    {
        var trade = TradeLink.FromData("Covens", ClusterStats);
        var check = PriceCheck.For(GameItem.Parse(ProjectileMedium)!, trade, 90, wantedNotables: ["Follow-Through"]);

        var socket = check.Filters.First(f => f.Label.EndsWith("is a Jewel Socket"));
        Assert.Equal("enchant.stat_sockets", socket.StatId);
        Assert.True(socket.Enabled);
        Assert.False(check.Filters.First(f => f.Label.StartsWith("Added Small Passive Skills also grant")).Enabled);
        // one wanted of two: the other place may be any projectile notable that keeps off its path
        Assert.NotNull(check.Cluster);
        Assert.Equal(["Follow-Through"], check.Cluster!.Wanted);
        Assert.Contains("Aerodynamics", check.Cluster.Others);
    }

    [Fact]
    public void A_cluster_copied_without_a_build_searches_all_its_notables()
    {
        var trade = TradeLink.FromData("Covens", ClusterStats);
        var check = PriceCheck.For(GameItem.Parse(ChaosCluster)!, trade, 90);
        Assert.Null(check.Cluster);
        Assert.All(["explicit.stat_ue", "explicit.stat_ug", "explicit.stat_wp"], id => Assert.True(check.Filters.First(f => f.StatId == id).Enabled));
    }

    [Fact]
    public void Ninja_exchange_divine_rate_is_read()
    {
        const string json = """{"lines":[{"id":"chaos","primaryValue":1},{"id":"divine","primaryValue":149.1}]}""";
        Assert.Equal(149.1, NinjaRates.ReadExchangeDivine(json));
    }
}
