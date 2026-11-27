using Shouldly;
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

        var baseActiveCount = worldA.BuildRenderSnapshot().ActivePopCount;

        worldA.AddPopForSimulation("prov_valenford", SocialClass.Peasants, "human", null, population: 30);
        worldB.AddPopForSimulation("prov_valenford", SocialClass.Peasants, "human", null, population: 30);

        worldA.StepDays(360);
        worldB.StepDays(360);

        var snapA = worldA.BuildRenderSnapshot();
        var snapB = worldB.BuildRenderSnapshot();

        snapA.Date.ShouldBe(snapB.Date);
        snapA.Treasury.ShouldBe(snapB.Treasury);
        snapA.TotalWorldPopulation.ShouldBe(snapB.TotalWorldPopulation);
        snapA.ActivePopCount.ShouldBe(baseActiveCount);
    }

    [Fact]
    public void ElvesExperienceMorteBrancaDeclineWhileNecoronasSupportsDualAncestryRuler()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, playerNationId: "NEC", seed: 1380UL);

        var before = world.BuildRenderSnapshot();
        before.Realm.RulerAncestryDisplay.ShouldBe("Vampiros + Humanos");
        before.Realm.DynastyName.ShouldBe("Casa Maledicta");
        before.Realm.RulerTraits.Count.ShouldBeInRange(2, 3);

        var elfBefore = before.States.Single(s => s.StateId == "state_ancient_woods").Population;
        world.StepDays(360);

        var after = world.BuildRenderSnapshot();
        var elfAfter = after.States.Single(s => s.StateId == "state_ancient_woods").Population;

        elfAfter.ShouldBeLessThan(elfBefore);
    }

    [Fact]
    public void SimulationWorld_FiveYearSoak_PreservesMathematicalIntegrity()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, playerNationId: "AET", seed: 13800501UL);

        var initialReport = world.AuditIntegrity();
        initialReport.IsHealthy.ShouldBeTrue(string.Join("; ", initialReport.Anomalies));
        initialReport.Date.ShouldBe(new GameDate(1380, 1, 1, 0));

        // 5 full calendar years = 5 * 360 days = 1800 days = 7200 sub-ticks
        world.StepDays(1800);

        var fiveYearReport = world.AuditIntegrity();
        fiveYearReport.IsHealthy.ShouldBeTrue(string.Join("; ", fiveYearReport.Anomalies));
        fiveYearReport.Date.ShouldBe(new GameDate(1385, 1, 1, 0));
        fiveYearReport.TotalTicksExecuted.ShouldBe(7200);
        fiveYearReport.MinPriceRatio.ShouldBeInRange(MarketPricing.MinMultiplier, MarketPricing.MaxMultiplier);
        fiveYearReport.MaxPriceRatio.ShouldBeInRange(MarketPricing.MinMultiplier, MarketPricing.MaxMultiplier);
    }
}
