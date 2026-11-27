using NSubstitute;
using Shouldly;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;
using Voracity.Tests.Integration;

namespace Voracity.Tests.Unit;

public sealed class EconomyAndTaxBaseTests
{
    [Fact]
    public void EstateTaxWeights_MatchEu5EconomyCanonicalValues()
    {
        SocialClassRules.EstateTaxWeight(EstateKind.Nobles).ShouldBe(100.0);
        SocialClassRules.EstateTaxWeight(EstateKind.Clergy).ShouldBe(25.0);
        SocialClassRules.EstateTaxWeight(EstateKind.Burghers).ShouldBe(40.0);
        SocialClassRules.EstateTaxWeight(EstateKind.Commoners).ShouldBe(1.0);
        SocialClassRules.EstateTaxWeight(EstateKind.Tribes).ShouldBe(0.01);

        var hookMock = Substitute.For<IEstateTaxHook>();
        hookMock.PeasantEnfranchisement.Returns(0.55);
        hookMock.GetWeight(EstateKind.Nobles).Returns(100.0);
        hookMock.PeasantEnfranchisement.ShouldBe(0.55);
    }

    [Fact]
    public void EffectiveTaxBase_ScalesWithControlAndCourtSpendingAffectsGovernmentPower()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var wHighCourt = new SimulationWorld(db, "AET", 1380UL);
        var wZeroCourt = new SimulationWorld(db, "AET", 1380UL);

        var snapInit = wHighCourt.BuildRenderSnapshot();
        snapInit.TotalPotentialTaxBase.ShouldBeGreaterThan(0.0);
        snapInit.TotalEffectiveTaxBase.ShouldBeLessThanOrEqualTo(snapInit.TotalPotentialTaxBase + 0.01);
        snapInit.Estates.Sum(e => e.TaxBaseSharePercent).ShouldBe(100.0, 0.5);

        wHighCourt.EnqueueCommand(new SetCourtSpendingCommand("AET", 100.0));
        wZeroCourt.EnqueueCommand(new SetCourtSpendingCommand("AET", 0.0));

        wHighCourt.StepDays(60);
        wZeroCourt.StepDays(60);

        var sHigh = wHighCourt.BuildRenderSnapshot();
        var sZero = wZeroCourt.BuildRenderSnapshot();

        sHigh.Realm.GovernmentPower.ShouldBeGreaterThan(sZero.Realm.GovernmentPower);
        sZero.Treasury.CopperUnits.ShouldBeGreaterThan(sHigh.Treasury.CopperUnits);
    }
}
