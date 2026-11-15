using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Tests.Integration;

public sealed class SaveLoadAndJournalIntegrationTests
{
    [Fact]
    public void SaveAndLoadRoundtrip_PreservesDeterministicFutureTicks()
    {
        var tempSavesDir = Path.Combine(Path.GetTempPath(), $"voracity_saves_{Guid.NewGuid():N}");
        try
        {
            var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
            var original = new SimulationWorld(db, playerNationId: "AET", seed: 777UL);

            original.EnqueueCommand(new BuildStructureCommand("state_aethelgard_crown", "building_university", 2));
            original.EnqueueCommand(new DiplomaticActionCommand("AET", "LEV", DiplomaticActionKind.ImproveRelations));
            original.StepDays(45);

            var saveData = original.ExportSaveData("test_campaign");
            var savedPath = SaveGameStorage.Save(saveData, tempSavesDir);

            var listed = SaveGameStorage.ListSaves(tempSavesDir);
            Assert.Single(listed);

            var reloaded = SimulationWorld.FromSaveData(db, SaveGameStorage.Load(savedPath));

            // Advancing both original and reloaded by 120 more days must match bit-for-bit
            original.StepDays(120);
            reloaded.StepDays(120);

            var snapOrig = original.BuildRenderSnapshot();
            var snapReload = reloaded.BuildRenderSnapshot();

            Assert.Equal(snapOrig.Date, snapReload.Date);
            Assert.Equal(snapOrig.Treasury, snapReload.Treasury);
            Assert.Equal(snapOrig.TotalWorldPopulation, snapReload.TotalWorldPopulation);
            Assert.Equal(snapOrig.ActiveTechId, snapReload.ActiveTechId);
        }
        finally
        {
            if (Directory.Exists(tempSavesDir))
                Directory.Delete(tempSavesDir, recursive: true);
        }
    }

    [Fact]
    public void WhiteDeathJournalEntry_CompletesAndHaltsElvenDecline()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, playerNationId: "ELF", seed: 1380UL);

        world.EnqueueCommand(new StartResearchCommand("ELF", "tech_hygiene_botany"));
        for (var i = 0; i < 7; i++)
            world.EnqueueCommand(new CourtActionCommand("ELF", CourtActionKind.ResearchGrant));

        world.StepDays(35);

        var snapAfterCure = world.BuildRenderSnapshot();
        Assert.Contains(snapAfterCure.PlayerJournalEntries, j => j.Id == "je_white_death_cure" && j.Completed);

        var elfPopAtCure = snapAfterCure.PlayerPopulation;
        world.StepDays(360);
        var snapOneYearLater = world.BuildRenderSnapshot();

        Assert.True(snapOneYearLater.PlayerPopulation > elfPopAtCure,
            $"Expected Elven population to recover after curing Morte Branca ({elfPopAtCure} -> {snapOneYearLater.PlayerPopulation}).");
    }
}
