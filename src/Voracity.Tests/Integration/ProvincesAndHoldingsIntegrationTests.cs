using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Tests.Integration;

public sealed class ProvincesAndHoldingsIntegrationTests
{
    [Fact]
    public void BaseDataset_LoadsNineteenStatesAndAllDetailedProvinces()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, "AET", 1380UL);
        var snap = world.BuildRenderSnapshot();

        Assert.Equal(19, snap.States.Count);
        Assert.True(snap.Provinces.Count >= 65, $"Expected detailed continent provinces, got {snap.Provinces.Count}");

        var aetCapitalProv = snap.Provinces.Single(p => p.StateId == "state_aethelgard_crown" && p.IsCapital);
        Assert.Equal(2, aetCapitalProv.HoldingLevel);
        Assert.True(aetCapitalProv.FortLevel >= 1);

        foreach (var state in snap.States)
        {
            Assert.NotEmpty(state.ProvinceIds);
            Assert.Equal(state.Provinces.Count, state.ProvinceIds.Count);
        }
    }

    [Fact]
    public void UpgradeProvinceHoldingAndFort_DeductsTreasuryIncreasesLevelsAndPersistsSaveLoad()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, "AET", 1380UL);

        var before = world.BuildRenderSnapshot();
        var targetProv = before.Provinces.First(p => p.OwnerNationId == "AET" && !p.IsCapital);

        world.EnqueueCommand(new UpgradeProvinceHoldingCommand(targetProv.ProvinceId, "holding"));
        world.EnqueueCommand(new UpgradeProvinceHoldingCommand(targetProv.ProvinceId, "fort"));
        world.StepSubTicks(1);

        var after = world.BuildRenderSnapshot();
        var updated = after.Provinces.Single(p => p.ProvinceId == targetProv.ProvinceId);

        Assert.Equal(targetProv.HoldingLevel + 1, updated.HoldingLevel);
        Assert.Equal(targetProv.FortLevel + 1, updated.FortLevel);
        Assert.True(after.Treasury.CopperUnits < before.Treasury.CopperUnits);

        // Save/Load roundtrip preserves province holding & fort levels
        var tempDir = Path.Combine(Path.GetTempPath(), $"voracity_prov_save_{Guid.NewGuid():N}");
        try
        {
            var savedPath = SaveGameStorage.Save(world.ExportSaveData("prov_test"), tempDir);
            var loadedSave = SaveGameStorage.Load(savedPath);
            var reloadedWorld = SimulationWorld.FromSaveData(db, loadedSave);
            var reloadedProv = reloadedWorld.BuildRenderSnapshot().Provinces.Single(p => p.ProvinceId == targetProv.ProvinceId);

            Assert.Equal(updated.HoldingLevel, reloadedProv.HoldingLevel);
            Assert.Equal(updated.FortLevel, reloadedProv.FortLevel);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WartimeProgression_OccupiesEnemyProvinceAndClearsOnPeace()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, "AET", 1380UL);

        world.EnqueueCommand(new DiplomaticActionCommand("AET", "CRZ", DiplomaticActionKind.DeclareWar));
        world.StepDays(60);

        var midWar = world.BuildRenderSnapshot();
        var crzDiplo = midWar.PlayerDiplomacy.Single(d => d.TargetNationId == "CRZ");
        Assert.True(crzDiplo.AtWar);
        Assert.Contains(midWar.Provinces, p => p.OccupiedByNationId != null);

        world.EnqueueCommand(new DiplomaticActionCommand("AET", "CRZ", DiplomaticActionKind.MakePeace));
        world.StepSubTicks(1);

        var postPeace = world.BuildRenderSnapshot();
        Assert.DoesNotContain(postPeace.Provinces, p => p.OccupiedByNationId != null);
    }
}
