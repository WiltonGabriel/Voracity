using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Mapping;

namespace Voracity.Tests.Integration;

public sealed class CanonicalPontariaDatasetTests
{
    [Fact]
    public void CanonicalDataset_PassesAtlasValidatorWithZeroErrorsAndFullHierarchy()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var atlas = new EditableWorldAtlas(db);

        // Ensure validation succeeds without structural, symmetry, or orphan errors
        var report = AtlasValidator.Validate(atlas);
        Assert.True(report.IsValid, string.Join(" | ", report.Issues.Select(i => $"{i.Code}:{i.Message}")));
        Assert.Equal(0, report.ErrorCount);

        // Verify 5-tier EU4-inspired geographic hierarchy
        Assert.Single(db.GeographyHierarchy.Continents);
        Assert.Equal("pontaria", db.GeographyHierarchy.Continents[0].Id);
        Assert.Equal(4, db.GeographyHierarchy.SuperRegions.Length);
        Assert.Equal(10, db.GeographyHierarchy.Regions.Length);
        Assert.Equal(36, db.GeographyHierarchy.Areas.Length);

        // Verify GeographyFeatures loaded from data/base/ including Eastern Bastion & organic Veasperia coast + islets
        Assert.True(db.GeographyFeatures.Landmasses.Length >= 7);
        var wastelandMasses = db.GeographyFeatures.Landmasses.Where(l => l.Kind == "wasteland_continent").ToArray();
        Assert.NotEmpty(wastelandMasses);
        Assert.Contains(wastelandMasses, w => w.Id == "lm_veasperia_wasteland");
        Assert.Contains(db.GeographyFeatures.MountainRanges, m => m.Id == "mtn_dead_ranges" && m.IsImpassable);
        Assert.NotEmpty(db.GeographyFeatures.Rivers);
        Assert.NotEmpty(db.GeographyFeatures.MountainRanges);
        Assert.NotEmpty(db.GeographyFeatures.Straits);

        // Zero playable provinces may lie inside any inaccessible beyond-wall wasteland landmass
        foreach (var st in db.States)
        {
            foreach (var p in st.ProvinceDetails ?? [])
            {
                foreach (var w in wastelandMasses)
                {
                    Assert.False(
                        PlanarMapTopology.PointInPolygon(w.PolygonPoints, (float)p.AnchorX, (float)p.AnchorY),
                        $"Playable province {p.Id} must stay west of the Bastion Wall!");
                }
            }
        }
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

        Assert.Equal("Ducado Senhorial", aetState.LocalAdminTitle);
        Assert.Equal("Domínio de Clã", bgfState.LocalAdminTitle);
        Assert.Equal("Prefeitura Imperial", hasState.LocalAdminTitle);

        // Invariant EU4 hierarchy fields are preserved on StateDefinition
        var rawAetState = db.States.First(s => s.Id == aetState.StateId);
        Assert.Equal("pontaria", rawAetState.ContinentId);
        Assert.False(string.IsNullOrWhiteSpace(rawAetState.SuperRegionId));
        Assert.False(string.IsNullOrWhiteSpace(rawAetState.RegionId));

        // Verify AdminProfiles & DynamicProvinceNames loaded
        Assert.True(db.AdminProfiles.Count >= 8);
        Assert.Contains(db.DynamicProvinceNames, r =>
            r.ProvinceId == "prov_averlon" && r.CultureOrNationId == "HAS" && r.LocalizedName == "Nova Lactopolis");
    }

    [Fact]
    public void EightOpeningJournalEntries_AreLoadedAndLocalizedInBothLanguages()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        Assert.Equal(8, db.JournalEntries.Count);

        foreach (var je in db.JournalEntries)
        {
            var ptTitle = db.Localization.Get("pt-BR", je.NameKey);
            var enTitle = db.Localization.Get("en-US", je.NameKey);
            var ptDesc = db.Localization.Get("pt-BR", je.DescriptionKey);
            var enDesc = db.Localization.Get("en-US", je.DescriptionKey);

            Assert.NotEqual(je.NameKey, ptTitle);
            Assert.NotEqual(je.NameKey, enTitle);
            Assert.NotEqual(je.DescriptionKey, ptDesc);
            Assert.NotEqual(je.DescriptionKey, enDesc);
        }
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

        Assert.Equal(s1.Date, s2.Date);
        Assert.Equal(s1.TotalWorldPopulation, s2.TotalWorldPopulation);
        Assert.Equal(s1.Treasury.CopperUnits, s2.Treasury.CopperUnits);
        Assert.Equal(s1.States.Count, s2.States.Count);
        Assert.Equal(s1.Provinces.Count, s2.Provinces.Count);
    }
}
