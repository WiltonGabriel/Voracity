using Shouldly;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Tests.Integration;

public sealed class ProvincesAndHoldingsIntegrationTests
{
    [Fact]
    public void BaseDataset_InitializesThirtySixAreasAndAllEu5LocationFields()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, "AET", 1380UL);
        var snap = world.BuildRenderSnapshot();

        snap.States.Count.ShouldBe(36);
        snap.Provinces.Count(p => p.Kind == LocationKind.Habitable).ShouldBe(134);
        snap.Provinces.Count(p => p.Kind == LocationKind.SeaZone).ShouldBeGreaterThanOrEqualTo(25);

        var aetCap = snap.Provinces.Single(p => p.StateId == "state_aethelgard_crown" && p.IsCapital);
        aetCap.Proximity.ShouldBe(100.0, 0.01);
        aetCap.Control.ShouldBeGreaterThan(80.0);
        aetCap.MarketId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void UpgradeLocationCommands_ExpandRgoUpgradeRoadAndRankCleanly()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, "AET", 1380UL);

        var before = world.BuildRenderSnapshot();
        var targetLoc = before.Provinces.First(p =>
            p.OwnerNationId == "AET" &&
            p.Kind == LocationKind.Habitable &&
            p.RgoLevel < p.MaxRgoLevel &&
            p.RoadLevel < RoadLevel.Railroad);

        world.EnqueueCommand(new UpgradeLocationCommand(targetLoc.ProvinceId, LocationUpgradeKind.ExpandRgo));
        world.EnqueueCommand(new UpgradeLocationCommand(targetLoc.ProvinceId, LocationUpgradeKind.UpgradeRoad));
        world.StepDays(31);

        var after = world.BuildRenderSnapshot();
        var updated = after.Provinces.Single(p => p.ProvinceId == targetLoc.ProvinceId);

        updated.RgoLevel.ShouldBe(targetLoc.RgoLevel + 1);
        updated.RoadLevel.ShouldBe(targetLoc.RoadLevel + 1);
    }
}
