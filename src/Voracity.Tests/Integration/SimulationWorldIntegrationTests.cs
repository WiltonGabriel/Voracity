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

    [Fact]
    public void SimulationWorld_FiveYearSoak_PreservesMathematicalIntegrityAndProgressesSystems()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, playerNationId: "AET", seed: 13800501UL);

        var initialReport = world.AuditIntegrity();
        Assert.True(initialReport.IsHealthy, string.Join("; ", initialReport.Anomalies));
        Assert.Equal(new GameDate(1380, 1, 1, 0), initialReport.Date);

        // 5 full calendar years = 5 * 360 days = 1800 days = 7200 sub-ticks
        world.StepDays(1800);

        var fiveYearReport = world.AuditIntegrity();
        Assert.True(fiveYearReport.IsHealthy, string.Join("; ", fiveYearReport.Anomalies));
        Assert.Equal(new GameDate(1385, 1, 1, 0), fiveYearReport.Date);
        Assert.Equal(7200, fiveYearReport.TotalTicksExecuted);
        Assert.True(fiveYearReport.CompletedTechCountTotal > 0, "Expected nations to complete technologies over 5 years.");
        Assert.InRange(fiveYearReport.MinPriceRatio, 0.25, 1.75);
        Assert.InRange(fiveYearReport.MaxPriceRatio, 0.25, 1.75);
    }
}
