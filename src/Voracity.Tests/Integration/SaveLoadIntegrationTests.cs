using Shouldly;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Tests.Integration;

public sealed class SaveLoadIntegrationTests
{
    [Fact]
    public void SaveAndLoadRoundtrip_PreservesDeterministicFutureTicksInSchemaV2()
    {
        var tempSavesDir = Path.Combine(Path.GetTempPath(), $"voracity_saves_{Guid.NewGuid():N}");
        try
        {
            var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
            var original = new SimulationWorld(db, playerNationId: "AET", seed: 777UL);

            original.EnqueueCommand(new BuildStructureCommand("prov_valenford", "building_university", 2));
            original.EnqueueCommand(new UpgradeLocationCommand("prov_briggston", LocationUpgradeKind.ExpandRgo));
            original.EnqueueCommand(new SetCourtSpendingCommand("AET", 80.0));
            original.StepDays(45);

            var saveData = original.ExportSaveData("test_campaign_v2");
            saveData.SchemaVersion.ShouldBe(2);

            var savedPath = SaveGameStorage.Save(saveData, tempSavesDir);
            SaveGameStorage.ListSaves(tempSavesDir).Count.ShouldBe(1);

            var reloaded = SimulationWorld.FromSaveData(db, SaveGameStorage.Load(savedPath));

            original.StepDays(120);
            reloaded.StepDays(120);

            var snapOrig = original.BuildRenderSnapshot();
            var snapReload = reloaded.BuildRenderSnapshot();

            snapOrig.Date.ShouldBe(snapReload.Date);
            snapOrig.Treasury.ShouldBe(snapReload.Treasury);
            snapOrig.TotalWorldPopulation.ShouldBe(snapReload.TotalWorldPopulation);
            snapOrig.AverageControl.ShouldBe(snapReload.AverageControl, 0.001);
        }
        finally
        {
            if (Directory.Exists(tempSavesDir))
                Directory.Delete(tempSavesDir, recursive: true);
        }
    }
}
