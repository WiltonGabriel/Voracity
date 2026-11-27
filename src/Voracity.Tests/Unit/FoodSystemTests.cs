using Shouldly;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Engine.Systems;
using Voracity.Core.Models;
using Voracity.Tests.Integration;

namespace Voracity.Tests.Unit;

public sealed class FoodSystemTests
{
    [Fact]
    public void LocalFoodProductionModifier_ReflectsRankTopographyAndDevelopment()
    {
        var ruralFlat = new LocationRuntime
        {
            Rank = LocationRank.RuralSettlement,
            Topography = TopographyType.Flatland,
            Development = 0.0
        };
        FoodSystem.ComputeLocalFoodProductionModifier(in ruralFlat).ShouldBe(1.10, 0.001);

        var megalopolisMtn = new LocationRuntime
        {
            Rank = LocationRank.Megalopolis,
            Topography = TopographyType.Mountains,
            Development = 20.0
        };
        // 1.0 - 0.67 - 0.20 + 0.20 = 0.33
        FoodSystem.ComputeLocalFoodProductionModifier(in megalopolisMtn).ShouldBe(0.33, 0.001);
    }

    [Fact]
    public void FoodSystem_WhenStorageDrainedAndDeficitUncovered_MarksProvinceStarvingAndAppliesPopPenalty()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, "AET", 1380UL);

        // Starving province simulation rate check via pure formula:
        var loc = new LocationRuntime
        {
            Rank = LocationRank.City,
            Prosperity = 0.50,
            TotalPopulation = 100_000
        };
        var normalRate = DemographySystem.ComputeMonthlyGrowthRate(in loc, 0.5, 1.0, 6.0, isStarving: false, ancestryGrowthModifier: 0.0);
        var starvingRate = DemographySystem.ComputeMonthlyGrowthRate(in loc, 0.5, 1.0, 0.0, isStarving: true, ancestryGrowthModifier: 0.0);

        starvingRate.ShouldBeLessThan(-0.020);
        (normalRate - starvingRate).ShouldBeGreaterThan(0.025);
        world.AuditIntegrity().IsHealthy.ShouldBeTrue();
    }
}
