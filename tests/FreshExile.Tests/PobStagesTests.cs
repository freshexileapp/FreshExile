using Xunit;
using FreshExile.Core;

namespace FreshExile.Tests;

public class PobStagesTests
{
    // a live PoB as a streamer keeps it: one tree, one gem set, one item set, changing as they play
    static string Live(int level, string nodes, string gem, string helmet) => $"""
        <PathOfBuilding>
          <Build level="{level}" className="Witch" ascendClassName="None"/>
          <Tree activeSpec="1"><Spec title="Main" nodes="{nodes}" treeVersion="3_26"/></Tree>
          <Skills activeSkillSet="1"><SkillSet id="1" title="Main"><Skill label=""><Gem nameSpec="{gem}" skillId="{gem}" level="1" enabled="true"/></Skill></SkillSet></Skills>
          <Items activeItemSet="1">
            <Item id="1">Rarity: RARE
        {helmet}
        Iron Hat</Item>
            <ItemSet id="1" title="Main"><Slot name="Helmet" itemId="1"/></ItemSet>
          </Items>
        </PathOfBuilding>
        """;

    [Fact]
    public void Guide_tags_pair_trees_with_the_gem_and_item_sets_named_differently()
    {
        static string Spec(string title, string nodes) => $"""<Spec title="{title}" nodes="{nodes}" treeVersion="3_26"/>""";
        static string Set(string title, string gem) =>
            $"""<SkillSet title="{title}"><Skill label=""><Gem nameSpec="{gem}" skillId="{gem}" level="1" enabled="true"/></Skill></SkillSet>""";
        var xml = $$"""
            <PathOfBuilding>
              <Build level="100" className="Templar" ascendClassName="None"/>
              <Tree activeSpec="1">{{Spec("Act 1 {1}", "1")}}{{Spec("lvl 80 Pinnacles {6}", "1,2")}}{{Spec("lvl 92 Farming {7}", "1,2,3")}}{{Spec("lvl 100 Endgame (Invert the rules) {9}", "1,2,3,4")}}</Tree>
              <Skills>{{Set("Act 1 {1}", "Smite")}}{{Set("Pinnacles {6,7}", "Raise Zombie")}}{{Set("Endgame {9}", "Absolution")}}</Skills>
              <Items><ItemSet id="1" title="Campaign {1,2,3,4,5}"/><ItemSet id="2" title="Endgame {9}"/></Items>
            </PathOfBuilding>
            """;
        var build = BuildParser.Parse(xml);

        // every stage has its tree: the sets no longer trail behind as tree-less stages
        Assert.Equal(["Act 1 {1}", "lvl 80 Pinnacles {6}", "lvl 92 Farming {7}", "lvl 100 Endgame (Invert the rules) {9}"],
            build.Stages.Select(s => s.Title));
        Assert.All(build.Stages, s => Assert.NotNull(s.Tree));
        Assert.Equal(["Smite", "Raise Zombie", "Raise Zombie", "Absolution"], build.Stages.Select(s => s.AllGems.Single().Name));
        Assert.Equal("Campaign {1,2,3,4,5}", build.GearFor(build.Stages[0])!.Value.Set.Title);
        Assert.Equal("Endgame {9}", build.GearFor(build.Stages[3])!.Value.Set.Title);
    }

    [Fact]
    public void Each_snapshot_of_a_live_build_becomes_its_own_stage_with_its_own_gems_and_gear()
    {
        var (a, o1, t1) = PobStages.Append(null, Live(12, "1,2", "Fireball", "Doom Crown"));
        Assert.Equal(PobStages.Outcome.Started, o1);
        Assert.Equal("Ур. 12", t1);
        Assert.True(PobStages.IsChain(a));

        var (b, o2, t2) = PobStages.Append(a, Live(38, "1,2,3,4", "Arc", "Grim Visor"));
        Assert.Equal(PobStages.Outcome.Appended, o2);
        Assert.Equal("Ур. 38", t2);

        var build = BuildParser.Parse(b);
        Assert.Equal(["Ур. 12", "Ур. 38"], build.Stages.Select(s => s.Title));
        Assert.Equal([1, 2], build.Stages[0].Tree!.Nodes);
        Assert.Equal("Fireball", build.Stages[0].AllGems.Single().Name);
        Assert.Equal("Arc", build.Stages[1].AllGems.Single().Name);
        Assert.Equal("Doom Crown", build.GearFor(build.Stages[0])!.Value.Set.Slots.Single().Item.Name);
        Assert.Equal("Grim Visor", build.GearFor(build.Stages[1])!.Value.Set.Slots.Single().Item.Name);
        Assert.Equal(38, build.Level);
    }

    [Fact]
    public void The_same_snapshot_twice_adds_nothing()
    {
        var (a, _, _) = PobStages.Append(null, Live(12, "1,2", "Fireball", "Doom Crown"));
        var (b, o, _) = PobStages.Append(a, Live(12, "2,1", "Fireball", "Doom Crown"));
        Assert.Equal(PobStages.Outcome.Unchanged, o);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Another_class_starts_a_new_guide()
    {
        var (a, _, _) = PobStages.Append(null, Live(12, "1,2", "Fireball", "Doom Crown"));
        var (b, o, _) = PobStages.Append(a, Live(20, "5", "Cleave", "Iron Hat").Replace("Witch", "Marauder"));
        Assert.Equal(PobStages.Outcome.Started, o);
        Assert.Single(BuildParser.Parse(b).Stages);
    }
}
