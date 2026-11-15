using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Tests.Integration;

public sealed class SimulationWorldIntegrationTests
{
    [Fact]
    public void SimulationWorld_IsDeterministicAndConsolidatesMicroPopsAcrossFullYear()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());

        var worldA = new SimulationWorld(db, playerNationId: "AET", seed: 424242UL);
        var worldB = new SimulationWorld(db, playerNationId: "AET", seed: 424242UL);

        worldA.AddPopForSimulation("state_aethelgard_crown", "peasants", "human", null, population: 30);
        worldB.AddPopForSimulation("state_aethelgard_crown", "peasants", "human", null, population: 30);

        worldA.StepDays(360);
        worldB.StepDays(360);

        var snapA = worldA.BuildRenderSnapshot();
        var snapB = worldB.BuildRenderSnapshot();

        Assert.Equal(snapA.Date, snapB.Date);
        Assert.Equal(snapA.Treasury, snapB.Treasury);
        Assert.Equal(snapA.TotalWorldPopulation, snapB.TotalWorldPopulation);
        Assert.Equal(db.States.Sum(s => s.InitialPops.Length), snapA.ActivePopCount);
    }

    [Fact]
    public void InvestStateFarmCommand_DeductsTreasuryAndIncreasesFarmLevel()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, playerNationId: "AET", seed: 1380UL);

        var before = world.BuildRenderSnapshot();
        var stateBefore = before.States.Single(s => s.StateId == "state_aethelgard_crown");

        world.EnqueueCommand(new InvestStateFarmCommand("state_aethelgard_crown", 2));
        world.StepSubTicks(1);

        var after = world.BuildRenderSnapshot();
        var stateAfter = after.States.Single(s => s.StateId == "state_aethelgard_crown");

        Assert.Equal(stateBefore.FarmLevels + 2, stateAfter.FarmLevels);
        Assert.Equal(before.Treasury - MonetaryAmount.FromGold(700.0), after.Treasury);
    }

    [Fact]
    public void ElvesExperienceMorteBrancaDeclineWhileNecoronasSupportsDualAncestryRuler()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, playerNationId: "NEC", seed: 1380UL);

        var before = world.BuildRenderSnapshot();
        Assert.Equal("Vampiros + Humanos", before.RulerAncestryDisplay);

        var elfBefore = before.States.Single(s => s.StateId == "state_ancient_woods").Population;
        world.StepDays(360);

        var after = world.BuildRenderSnapshot();
        var elfAfter = after.States.Single(s => s.StateId == "state_ancient_woods").Population;

        Assert.True(elfAfter < elfBefore, $"Expected Elven population decline from Morte Branca ({elfBefore} -> {elfAfter}).");
    }
}
