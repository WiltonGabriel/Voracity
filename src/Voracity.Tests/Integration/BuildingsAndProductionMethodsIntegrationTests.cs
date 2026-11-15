using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Tests.Integration;

public sealed class BuildingsAndProductionMethodsIntegrationTests
{
    [Fact]
    public void BuildingExpansionAndTechGatedPmSwitching_WorksEndToEnd()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, playerNationId: "HAS", seed: 1380UL);

        var before = world.BuildRenderSnapshot();
        var hastaCore = before.States.Single(s => s.StateId == "state_hasta_core");
        var millsBefore = hastaCore.Buildings.Single(b => b.BuildingId == "building_steel_mills");

        // Expand Steel Mills by 2 levels (650 Gold * 2 = 1300 Gold)
        world.EnqueueCommand(new BuildStructureCommand("state_hasta_core", "building_steel_mills", 2));
        // Attempt to switch PM before researching tech_steelworking => should stay on charcoal forges
        world.EnqueueCommand(new SwitchProductionMethodCommand("state_hasta_core", "building_steel_mills", "pm_coal_blast_furnaces"));
        world.StepSubTicks(1);

        var mid = world.BuildRenderSnapshot();
        var millsMid = mid.States.Single(s => s.StateId == "state_hasta_core").Buildings.Single(b => b.BuildingId == "building_steel_mills");

        Assert.Equal(millsBefore.Levels + 2, millsMid.Levels);
        Assert.Equal("pm_charcoal_forges", millsMid.ActivePmId);
        Assert.Equal(before.Treasury - MonetaryAmount.FromGold(1300.0), mid.Treasury);

        // Research tech_steelworking via funding grants, then switch PM
        world.EnqueueCommand(new StartResearchCommand("HAS", "tech_steelworking"));
        for (var i = 0; i < 4; i++)
            world.EnqueueCommand(new CourtActionCommand("HAS", CourtActionKind.ResearchGrant));
        world.StepSubTicks(1);

        world.EnqueueCommand(new SwitchProductionMethodCommand("state_hasta_core", "building_steel_mills", "pm_coal_blast_furnaces"));
        world.StepDays(7);

        var after = world.BuildRenderSnapshot();
        var millsAfter = after.States.Single(s => s.StateId == "state_hasta_core").Buildings.Single(b => b.BuildingId == "building_steel_mills");
        Assert.Equal("pm_coal_blast_furnaces", millsAfter.ActivePmId);
    }
}
