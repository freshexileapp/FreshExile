using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class TreeOverrideTests
{
    [Fact]
    public void Tattoos_and_runegrafts_are_read_from_the_specs_overrides()
    {
        // PoB writes each replaced passive as <Override nodeId dn icon …> with one stat line per text line
        var build = BuildParser.Parse("""
            <PathOfBuilding><Build className="Templar" level="95"/>
              <Tree activeSpec="1"><Spec title="Maps" nodes="100,200,300" masteryEffects="{300,7}">
                <Overrides>
                  <Override nodeId="100" dn="Tattoo of the Kitava Blood Drinker" icon="x.png">
                    0.5% of Attack Damage Leeched as Life
                  </Override>
                  <Override nodeId="200" dn="Runegraft of the Warp" icon="y.png">
                    Buffs on you expire 30% slower
                    Debuffs on you expire 30% faster
                  </Override>
                  <Override dn="Tattoo with no node"/>
                </Overrides>
              </Spec></Tree><Skills/>
            </PathOfBuilding>
            """);

        var o = build.Stages[0].Tree!.Overrides;
        Assert.Equal(2, o.Count);
        Assert.True(o[0].IsTattoo);
        Assert.Equal(["0.5% of Attack Damage Leeched as Life"], o[0].Lines);
        Assert.True(o[1].IsRunegraft);
        Assert.Equal(["Buffs on you expire 30% slower", "Debuffs on you expire 30% faster"], o[1].Lines);
    }
}
