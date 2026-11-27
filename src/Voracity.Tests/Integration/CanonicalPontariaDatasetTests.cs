using Shouldly;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Tests.Integration;

public sealed class CanonicalPontariaDatasetTests
{
    [Fact]
    public void CanonicalDataset_PassesAtlasValidatorWithZeroErrorsZeroWarningsAndAllLocationKinds()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var atlas = new EditableWorldAtlas(db);

        var report = AtlasValidator.Validate(atlas);
        var diagnostic = string.Join(" | ", report.Issues.Select(i => $"{i.Severity}:{i.Code}:{i.Message}"));
        report.IsValid.ShouldBeTrue(diagnostic);
        report.ErrorCount.ShouldBe(0, diagnostic);
        report.WarningCount.ShouldBe(0, diagnostic);

        db.GeographyHierarchy.Continents.Length.ShouldBe(1);
        db.GeographyHierarchy.Continents[0].Id.ShouldBe("pontaria");
        db.GeographyHierarchy.SuperRegions.Length.ShouldBe(4);
        db.GeographyHierarchy.Regions.Length.ShouldBe(10);
        db.GeographyHierarchy.Areas.Length.ShouldBe(36);
        db.Goods.Count.ShouldBe(34);
        db.Markets.Count.ShouldBe(6);

        var allLocations = db.BuildAllLocations();
        allLocations.Count(l => l.Kind == LocationKind.Habitable).ShouldBe(134);
        allLocations.Count(l => l.Kind == LocationKind.Impassable).ShouldBeGreaterThanOrEqualTo(3);
        allLocations.Count(l => l.Kind == LocationKind.Corridor).ShouldBeGreaterThanOrEqualTo(3);
        allLocations.Count(l => l.Kind == LocationKind.SeaZone).ShouldBeGreaterThanOrEqualTo(25);

        atlas.Topology.FaceByProvinceId["prov_briggston"].IsCoastal.ShouldBeFalse();
        atlas.Topology.Faces.Count(f => f.Kind == LocationKind.Habitable && f.IsCoastal).ShouldBeGreaterThan(20);
        db.GeographyFeatures.EnsureBarriers().Length.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void CulturalAdminProfiles_AdaptLocalTitlesWithoutMutatingInvariantGeography()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, "AET", 1380UL);
        var snap = world.BuildRenderSnapshot();

        var aetState = snap.States.First(s => s.OwnerNationId == "AET");
        var bgfState = snap.States.First(s => s.OwnerNationId == "BGF");
        var hasState = snap.States.First(s => s.OwnerNationId == "HAS");

        aetState.LocalAdminTitle.ShouldBe("Ducado Senhorial");
        bgfState.LocalAdminTitle.ShouldBe("Domínio de Clã");
        hasState.LocalAdminTitle.ShouldBe("Prefeitura Imperial");

        var rawAetState = db.States.First(s => s.Id == aetState.StateId);
        rawAetState.ContinentId.ShouldBe("pontaria");
        rawAetState.SuperRegionId.ShouldNotBeNullOrWhiteSpace();
        rawAetState.RegionId.ShouldNotBeNullOrWhiteSpace();

        db.AdminProfiles.Count.ShouldBeGreaterThanOrEqualTo(8);
        db.DynamicProvinceNames.ShouldContain(r =>
            r.ProvinceId == "prov_averlon" && r.CultureOrNationId == "HAS" && r.LocalizedName == "Nova Lactopolis");
    }

    [Fact]
    public void CanonicalDataset_365DaySimulation_IsStrictlyDeterministic()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var w1 = new SimulationWorld(db, "AET", 1380UL);
        var w2 = new SimulationWorld(db, "AET", 1380UL);

        w1.StepDays(365);
        w2.StepDays(365);

        var s1 = w1.BuildRenderSnapshot();
        var s2 = w2.BuildRenderSnapshot();

        s1.Date.ShouldBe(s2.Date);
        s1.TotalWorldPopulation.ShouldBe(s2.TotalWorldPopulation);
        s1.Treasury.CopperUnits.ShouldBe(s2.Treasury.CopperUnits);
        s1.AverageControl.ShouldBe(s2.AverageControl);
        s1.Provinces.Count.ShouldBe(s2.Provinces.Count);
    }

    [Fact]
    public Task CanonicalInitialWorld_MatchesVerifiedSnapshot()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, "AET", 1380UL);
        var snap = world.BuildRenderSnapshot();

        var summary = new
        {
            Date = snap.Date.ToDisplayString(),
            NationId = snap.Realm.NationId,
            Dynasty = snap.Realm.DynastyName,
            Traits = snap.Realm.RulerTraits,
            TotalLocations = snap.Provinces.Count,
            Habitable = snap.Provinces.Count(p => p.Kind == LocationKind.Habitable),
            Impassable = snap.Provinces.Count(p => p.Kind == LocationKind.Impassable),
            Corridor = snap.Provinces.Count(p => p.Kind == LocationKind.Corridor),
            SeaZones = snap.Provinces.Count(p => p.Kind == LocationKind.SeaZone),
            Markets = snap.Markets.Select(m => m.MarketId).ToArray(),
            AreasCount = snap.States.Count
        };

        return Verify(summary);
    }
}
