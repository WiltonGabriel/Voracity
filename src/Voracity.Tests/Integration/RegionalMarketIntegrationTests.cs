using Shouldly;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Tests.Integration;

public sealed class RegionalMarketIntegrationTests
{
    [Fact]
    public void LocationsConnectToSixRegionalMarketsAndCenterEnjoysFullAccess()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, "AET", 1380UL);

        world.StepDays(30);
        var snap = world.BuildRenderSnapshot();

        snap.Markets.Count.ShouldBe(6);
        foreach (var m in snap.Markets)
        {
            m.LocationCount.ShouldBeGreaterThan(0);
            var centerLoc = snap.Provinces.Single(p => p.ProvinceId == m.CenterLocationId);
            centerLoc.IsMarketCenter.ShouldBeTrue();
            centerLoc.MarketAccess.ShouldBe(1.0, 0.001);
        }

        // All habitable locations belong to a valid market and produce PotentialTaxBase >= EffectiveTaxBase
        foreach (var loc in snap.Provinces.Where(p => p.Kind == LocationKind.Habitable))
        {
            loc.MarketId.ShouldNotBeNullOrWhiteSpace();
            loc.MarketAccess.ShouldBeInRange(0.25, 1.0);
            loc.EffectiveTaxBase.ShouldBeLessThanOrEqualTo(loc.PotentialTaxBase + 0.001);
        }
    }
}
