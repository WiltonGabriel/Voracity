using Shouldly;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Tests.Integration;

public sealed class BuildingsAndProductionMethodsIntegrationTests
{
    [Fact]
    public void LocationBuildingExpansionAndPmSwitching_UpdatesOutputAndDeductsTreasury()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, playerNationId: "HAS", seed: 1380UL);

        var before = world.BuildRenderSnapshot();
        var targetLoc = before.Provinces.Single(p => p.ProvinceId == "prov_hastapolis");
        var millsBefore = targetLoc.Buildings!.Single(b => b.BuildingId == "building_steel_mills");

        // Expand Steel Mills in prov_hastapolis by 2 levels (650 Gold * 2 = 1300 Gold) + switch PM
        world.EnqueueCommand(new BuildStructureCommand("prov_hastapolis", "building_steel_mills", 2));
        world.EnqueueCommand(new SwitchProductionMethodCommand("prov_hastapolis", "building_steel_mills", "pm_coal_blast_furnaces"));
        world.StepSubTicks(1);

        var after = world.BuildRenderSnapshot();
        var millsAfter = after.Provinces.Single(p => p.ProvinceId == "prov_hastapolis").Buildings!.Single(b => b.BuildingId == "building_steel_mills");

        millsAfter.Levels.ShouldBe(millsBefore.Levels + 2);
        millsAfter.ActivePmId.ShouldBe("pm_coal_blast_furnaces");
        after.Treasury.ShouldBe(before.Treasury - MonetaryAmount.FromGold(1300.0));
    }
}
