using Voracity.Core.Models;

namespace Voracity.Tests.Unit;

public sealed class GameDateAndDualAncestryTests
{
    private static readonly AncestryDefinition[] SampleCatalog =
    [
        new("human", "Humanos", 0.15, 0.10, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, false, true, "#D4A373"),
        new("orc", "Orcs", 0.10, -0.05, 0.10, 0.25, 0.0, -0.10, 0.0, 1.15, false, true, "#5E7C3A"),
        new("vampire", "Vampiros", 0.0, 0.05, 0.0, 0.20, 0.0, 0.15, -0.15, 0.0, true, true, "#6E1326")
    ];

    [Fact]
    public void GameDate_RollsOverSubTicksDaysMonthsAndYears()
    {
        var date = new GameDate(1380, 12, 30, 3);
        var next = date.AdvanceOneSubTick();
        Assert.Equal(new GameDate(1381, 1, 1, 0), next);
        Assert.Equal("01/01/1381 D.Q. (00:00)", next.ToDisplayString());
    }

    [Fact]
    public void DualAncestry_BlendsMixedBiologicalTraitsAndBloodDependency()
    {
        var pureHuman = new DualAncestry(0);
        Assert.False(pureHuman.IsMixed);
        Assert.Equal("Humanos", pureHuman.FormatName(SampleCatalog));

        var halfOrc = new DualAncestry(0, 1);
        var halfOrcTraits = DualAncestry.ComputeEffectiveTraits(halfOrc, SampleCatalog);
        Assert.True(halfOrc.IsMixed);
        Assert.Equal("Humanos + Orcs", halfOrc.FormatName(SampleCatalog));
        Assert.Equal(0.025, halfOrcTraits.TechSpreadModifier, 4);
        Assert.Equal(0.125, halfOrcTraits.MartialPowerModifier, 4);
        Assert.Equal(1.075, halfOrcTraits.FoodCalorieMultiplier, 4);
        Assert.False(halfOrcTraits.RequiresBloodEssence);

        var dhampir = new DualAncestry(2, 0);
        var dhampirTraits = DualAncestry.ComputeEffectiveTraits(dhampir, SampleCatalog);
        Assert.True(dhampirTraits.RequiresBloodEssence);
        Assert.Equal(0.5, dhampirTraits.FoodCalorieMultiplier, 4);
    }
}
