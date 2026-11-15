using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Tests.Integration;

public sealed class PoliticsTechAndDiplomacyIntegrationTests
{
    [Fact]
    public void InterestGroupsSumTo100AndLawEnactmentCompletesCleanly()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, playerNationId: "AET", seed: 1380UL);

        var snap = world.BuildRenderSnapshot();
        Assert.Equal(8, snap.PlayerInterestGroups.Count);
        var cloutSum = snap.PlayerInterestGroups.Sum(ig => ig.CloutPercent);
        Assert.InRange(cloutSum, 99.5, 100.5);

        // Enact Interventionism (takes ~3-4 monthly ticks)
        world.EnqueueCommand(new EnactLawCommand("AET", "law_interventionism"));
        world.StepDays(150);

        var after = world.BuildRenderSnapshot();
        Assert.Contains(after.PlayerLaws, l => l.Id == "law_interventionism" && l.IsEnacted);
        Assert.DoesNotContain(after.PlayerLaws, l => l.Id == "law_feudal_agrarianism" && l.IsEnacted);
    }

    [Fact]
    public void CourtBanquetReducesStressAndDiplomacyWarProgressesWeekly()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, playerNationId: "HAS", seed: 999UL);

        world.EnqueueCommand(new CourtActionCommand("HAS", CourtActionKind.RaiseAuthority));
        world.EnqueueCommand(new DiplomaticActionCommand("HAS", "AET", DiplomaticActionKind.DeclareWar));
        world.StepSubTicks(1);

        var duringWar = world.BuildRenderSnapshot();
        var warRel = duringWar.PlayerDiplomacy.Single(d => d.TargetNationId == "AET");
        Assert.True(warRel.AtWar);
        Assert.True(duringWar.RulerStress > 0);

        world.EnqueueCommand(new CourtActionCommand("HAS", CourtActionKind.RoyalBanquet));
        world.StepDays(14);

        var postBanquet = world.BuildRenderSnapshot();
        Assert.True(postBanquet.RulerStress < duringWar.RulerStress);
        var warUpdated = postBanquet.PlayerDiplomacy.Single(d => d.TargetNationId == "AET");
        Assert.NotEqual(0.0, warUpdated.WarScore);
    }
}
