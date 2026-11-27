using Shouldly;
using Voracity.Core.Engine;
using Voracity.Core.Engine.Systems;
using Voracity.Core.Models;

namespace Voracity.Tests.Unit;

public sealed class ProximityAndControlTests
{
    [Fact]
    public void LandProximityCost_FollowsEu5RoadVsRiverMutualExclusionAndTerrainDivisor()
    {
        var flatTarget = new LocationRuntime
        {
            Kind = LocationKind.Habitable,
            Topography = TopographyType.Flatland,
            Vegetation = VegetationType.Grasslands,
            Development = 0.0
        };
        var mountainForestTarget = flatTarget with
        {
            Topography = TopographyType.Mountains, // -50%
            Vegetation = VegetationType.Forest     // -25% (ignored along road or river!)
        };

        // Base land cost = 40
        var costFlat = ProximityAndControlSystem.ComputeStepProximityCost(in flatTarget, in flatTarget, new LocationEdgeInfo(LocationEdgeKind.LandLand), 0.0);
        costFlat.ShouldBe(40.0, 0.001);

        // Gravel road (-20 => 20) vs Adjacent river (-28 => 12): mutually exclusive max reduction wins (12)!
        var costBoth = ProximityAndControlSystem.ComputeStepProximityCost(
            in flatTarget,
            in flatTarget,
            new LocationEdgeInfo(LocationEdgeKind.LandLand, HasRiver: true, RoadLevel: RoadLevel.GravelRoad),
            0.0);
        costBoth.ShouldBe(12.0, 0.001);

        // Mountain (-50%) with NO road/river + Forest (-25%): totalSpeed = -0.75 => divisor = 0.25 => 40 / 0.25 = 160!
        var costMtnForest = ProximityAndControlSystem.ComputeStepProximityCost(
            in flatTarget,
            in mountainForestTarget,
            new LocationEdgeInfo(LocationEdgeKind.LandLand),
            0.0);
        costMtnForest.ShouldBe(160.0, 0.001);

        // With Gravel road on Mountains+Forest: vegetation (-25%) ignored! totalSpeed = -0.50 => 20 / 0.50 = 40!
        var costMtnRoad = ProximityAndControlSystem.ComputeStepProximityCost(
            in flatTarget,
            in mountainForestTarget,
            new LocationEdgeInfo(LocationEdgeKind.LandLand, RoadLevel: RoadLevel.GravelRoad),
            0.0);
        costMtnRoad.ShouldBe(40.0, 0.001);
    }

    [Fact]
    public void SeaAndHarborProximityCost_FollowsEu5MaritimePresenceAndHarborScaling()
    {
        var port100 = new LocationRuntime { Kind = LocationKind.Habitable, NaturalHarborSuitability = 100 };
        var seaOpen = new LocationRuntime { Kind = LocationKind.SeaZone, SeaZoneMaritimePresence = 0.0 };
        var seaControlled = new LocationRuntime { Kind = LocationKind.SeaZone, SeaZoneMaritimePresence = 1.0 };

        // Coast <-> SeaZone base 40 scaled by -50% at 100% harbor capacity = 20
        ProximityAndControlSystem.ComputeStepProximityCost(in port100, in seaControlled, new LocationEdgeInfo(LocationEdgeKind.CoastSea), 0.0)
            .ShouldBe(20.0, 0.001);

        // SeaZone <-> SeaZone: 30 at 0% maritime presence, 5 at 100% maritime presence
        ProximityAndControlSystem.ComputeStepProximityCost(in seaOpen, in seaControlled, new LocationEdgeInfo(LocationEdgeKind.SeaSea), 0.0)
            .ShouldBe(30.0, 0.001);
        ProximityAndControlSystem.ComputeStepProximityCost(in seaControlled, in seaOpen, new LocationEdgeInfo(LocationEdgeKind.SeaSea), 0.0)
            .ShouldBe(5.0, 0.001);
    }

    [Fact]
    public void MaxControl_MatchesEu5FormulaForProximityIntegrationRankAndSatisfaction()
    {
        // Capital: 100 prox (75) + Core (+20) + City (+10) + 100% sat (0) = clamped 100
        ProximityAndControlSystem.ComputeMaxControl(100.0, IntegrationStatus.Core, LocationRank.City, 100.0)
            .ShouldBe(100.0, 0.001);

        // Unhappy conquered rural frontier at 40 prox: 0.75*40 (30) - Conquered (-10) + Rural (0) - 50% sat (-5) = 15
        ProximityAndControlSystem.ComputeMaxControl(40.0, IntegrationStatus.Conquered, LocationRank.RuralSettlement, 50.0)
            .ShouldBe(15.0, 0.001);
    }
}
