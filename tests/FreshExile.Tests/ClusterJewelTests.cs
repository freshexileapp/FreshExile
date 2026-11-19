using System.Text;
using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class ClusterJewelTests
{
    // one large cluster socket (index 3), taken, as GGG's export writes it
    const string TreeJson = """
        {"constants":{"skillsPerOrbit":[1,6,16,16,40],"orbitRadii":[0,82,162,335,493]},
         "groups":{"1":{"x":0,"y":0}},
         "nodes":{"500":{"name":"Large Jewel Socket","group":1,"orbit":0,"orbitIndex":0,"isJewelSocket":true,
                         "expansionJewel":{"size":2,"index":3,"proxy":"501","parent":null}}}}
        """;

    const string Pob = """
        <PathOfBuilding>
          <Build level="80" className="Marauder" ascendClassName="None"/>
          <Tree activeSpec="1"><Spec title="Maps" nodes="500,65760,65762,65770">
            <Sockets><Socket nodeId="500" itemId="7"/><Socket nodeId="600" itemId="8"/></Sockets>
          </Spec></Tree>
          <Skills/>
          <Items>
            <Item id="7">Rarity: RARE
        Brood Spark
        Large Cluster Jewel
        Implicits: 3
        {crafted}Adds 8 Passive Skills
        {crafted}2 Added Passive Skills are Jewel Sockets
        {crafted}Added Small Passive Skills grant: 12% increased Fire Damage
        1 Added Passive Skill is Wish for Death
        1 Added Passive Skill is Feed the Fury
        1 Added Passive Skill is Prodigious Defence</Item>
            <Item id="8">Rarity: RARE
        Gloom Eye
        Cobalt Jewel
        Implicits: 0
        +12 to maximum Life</Item>
          </Items>
        </PathOfBuilding>
        """;

    static readonly string[] ChaosLarge = ["Unholy Grace", "Unwaveringly Evil", "Wicked Pall"];

    [Fact]
    public void Smart_search_keeps_the_wanted_notables_by_the_entry_and_lets_the_third_be_any_that_sits_opposite()
    {
        // sort order: Unwaveringly Evil 11403 < Unholy Grace 11406 < Wicked Pall 11407 → places 2, 6, 10 of the ring;
        // 2 and 10 lie either side of the entry, 6 opposite: the third must sort between the two wanted ones
        var plan = ClusterJewel.Plan("Large Cluster Jewel", 8, 2, ChaosLarge, ["Unwaveringly Evil", "Wicked Pall"])!;
        Assert.Equal(["Unwaveringly Evil", "Wicked Pall"], plan.Wanted);
        Assert.Equal(1, plan.Count);
        Assert.Equal(["Unspeakable Gifts", "Dark Ideation", "Unholy Grace"], plan.Others);
        // Grim Oath, Overwhelming Malice, Touch of Cruelty sort first: they would take place 2, on the way to the rest
        Assert.Equal(3, plan.Skipped);
    }

    static PobItem LargeChaos() => BuildParser.Parse("""
        <PathOfBuilding><Build level="80" className="Ranger" ascendClassName="None"/><Tree activeSpec="1"><Spec title="x" nodes=""/></Tree>
        <Skills/><Items><Item id="1">Rarity: RARE
        Luminous Spark
        Large Cluster Jewel
        Implicits: 3
        {crafted}Adds 8 Passive Skills
        {crafted}2 Added Passive Skills are Jewel Sockets
        {crafted}Added Small Passive Skills grant: 12% increased Chaos Damage
        1 Added Passive Skill is Unholy Grace
        1 Added Passive Skill is Unwaveringly Evil
        1 Added Passive Skill is Wicked Pall</Item></Items></PathOfBuilding>
        """).Items.Values.First();

    [TreeFact]
    public void Every_large_socket_grows_its_cluster_with_the_entrance_facing_the_socket()
    {
        using var f = File.OpenRead(TreeRepository.CachePath);
        var tree = PassiveTree.Load(f);
        var sockets = tree.Nodes.Values.Where(n => n.ExpansionSize == 2 && n.ExpansionParent == null && n.Drawable).ToList();
        Assert.NotEmpty(sockets);
        foreach (var s in sockets)
        {
            var grown = tree.WithClusters([(s.Id, LargeChaos())]);
            var ring = grown.Nodes.Values.Where(n => n.IsClusterNode && n.Group == grown.Nodes[s.ExpansionProxy!.Value].Group).ToList();
            // 8 passives: 3 notables, 2 sockets, 3 small ones, all drawn and linked into a ring
            Assert.Equal(8, ring.Count);
            Assert.Equal(3, ring.Count(n => n.Kind == NodeKind.Notable));
            Assert.Equal(2, ring.Count(n => n.Kind == NodeKind.Jewel));
            Assert.All(ring, n => Assert.True(n.Drawable));
            // the entrance (place 0) is the ring's passive nearest the socket, and the socket links to it
            var entrance = ring.Single(n => grown.Nodes[s.Id].Links.Contains(n.Id));
            var nearest = ring.MinBy(n => Math.Pow(n.X - s.X, 2) + Math.Pow(n.Y - s.Y, 2))!;
            Assert.Equal(nearest.Id, entrance.Id);
            Assert.Contains(grown.Edges, e => (e.A == entrance.Id && e.B == s.Id) || (e.A == s.Id && e.B == entrance.Id));
            // the base tree is left as it was
            Assert.DoesNotContain(tree.Nodes.Values, n => n.IsClusterNode);
        }
    }

    [TreeFact]
    public void A_grown_cluster_carries_the_games_art_for_the_overlay()
    {
        using var f = File.OpenRead(TreeRepository.CachePath);
        var tree = PassiveTree.Load(f);
        var s = tree.Nodes.Values.First(n => n.ExpansionSize == 2 && n.ExpansionParent == null && n.Drawable);
        var grown = tree.WithClusters([(s.Id, LargeChaos())]);
        Assert.Equal("Large Cluster Jewel", grown.Clusters[s.Id].BaseType);
        var ring = grown.Nodes.Values.Where(n => n.IsClusterNode && n.Group == grown.Nodes[s.ExpansionProxy!.Value].Group).ToList();
        // every notable and small passive has an icon the export's sheets hold, at the size the game draws it
        var normal = grown.Sprites["normalActive"];
        var notable = grown.Sprites["notableActive"];
        Assert.All(ring.Where(n => n.Kind == NodeKind.Normal), n => Assert.True(normal.Coords.ContainsKey(n.Icon), n.Icon));
        Assert.All(ring.Where(n => n.Kind == NodeKind.Notable), n => Assert.True(notable.Coords.ContainsKey(n.Icon), n.Icon));
        Assert.True(grown.Sprites["frame"].Coords.ContainsKey("PSSkillFrame"));
        Assert.True(normal.Zoom > 0 && normal.Url.StartsWith("https://", StringComparison.Ordinal));
    }

    [Fact]
    public void A_small_cluster_passive_finds_its_icon_by_what_it_grants_whatever_the_numbers()
    {
        Assert.Equal("Art/2DArt/SkillIcons/passives/AreaDmgNode.png", ClusterGraph.SmallIcon(["12% increased Area Damage"]));
        Assert.Equal("", ClusterGraph.SmallIcon([]));
        Assert.Equal("", ClusterGraph.SmallIcon(["Nothing like this"]));
    }

    [Fact]
    public void A_crafted_implicit_is_an_enchant_as_pob_writes_anointments_and_cluster_bases()
    {
        var mods = LargeChaos().Mods;
        Assert.Equal(ModKind.Enchant, mods.First(m => m.Text == "Adds 8 Passive Skills").Kind);
        Assert.Equal(ModKind.Explicit, mods.First(m => m.Text.EndsWith("Wicked Pall")).Kind);
    }

    [TreeFact]
    public void An_anointment_knows_its_three_oils_from_the_tree_export()
    {
        using var f = File.OpenRead(TreeRepository.CachePath);
        var tree = PassiveTree.Load(f);
        Assert.Equal(["ClearOil", "AmberOil", "CrimsonOil"], tree.AnointRecipe("Toxic Strikes"));
        Assert.Null(tree.AnointRecipe("Not A Notable"));
    }

    [Fact]
    public void Smart_search_has_nothing_to_choose_when_every_notable_is_wanted()
    {
        Assert.Null(ClusterJewel.Plan("Large Cluster Jewel", 8, 2, ChaosLarge, ChaosLarge));
        Assert.Null(ClusterJewel.Plan("Cobalt Jewel", 8, 2, ChaosLarge, ["Wicked Pall"]));
    }

    [Fact]
    public void A_cluster_tells_which_notables_the_tree_takes_and_unused_sockets_are_left_out()
    {
        var build = BuildParser.Parse(Pob);
        var jewels = build.JewelsFor(build.Stages[0]);
        // the cobalt jewel sits in socket 600, which the tree does not take
        var (socket, item) = Assert.Single(jewels);
        Assert.Equal(500, socket);
        Assert.True(ClusterJewel.IsCluster(item));

        var tree = PassiveTree.Load(new MemoryStream(Encoding.UTF8.GetBytes(TreeJson)));
        var a = ClusterJewel.Allocation(item, socket, tree, build.Stages[0].Tree!.Nodes)!;
        // PoB's order (notableSortOrder) puts them at indexes 2, 6, 10 of the large cluster: ids 0x10000 + 3<<6 + 2<<4 + i
        Assert.Equal(["Prodigious Defence", "Feed the Fury", "Wish for Death"], a.Notables.Select(n => n.Name));
        Assert.Equal([65762, 65766, 65770], a.Notables.Select(n => n.NodeId));
        Assert.Equal([true, false, true], a.Notables.Select(n => n.Taken));
        Assert.Equal(1, a.SmallTaken);
        Assert.Equal(3, a.SmallTotal);
    }
}
