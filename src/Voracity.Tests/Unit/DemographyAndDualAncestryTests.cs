using Shouldly;
using Voracity.Core.Engine;
using Voracity.Core.Engine.Systems;
using Voracity.Core.Models;

namespace Voracity.Tests.Unit;

public sealed class DemographyAndDualAncestryTests
{
    [Fact]
    public void SocialClasses_MapAllEightEu5ClassesAndFoodDemands()
    {
        Enum.GetValues<SocialClass>().Length.ShouldBe(8);
        SocialClassRules.FoodDemandPer1KPops(SocialClass.Nobles).ShouldBe(20.0);
        SocialClassRules.FoodDemandPer1KPops(SocialClass.Clerics).ShouldBe(5.0);
        SocialClassRules.FoodDemandPer1KPops(SocialClass.Burghers).ShouldBe(4.0);
        SocialClassRules.FoodDemandPer1KPops(SocialClass.Soldiers).ShouldBe(5.0);
        SocialClassRules.FoodDemandPer1KPops(SocialClass.Peasants).ShouldBe(1.0);
        SocialClassRules.FoodDemandPer1KPops(SocialClass.Laborers).ShouldBe(1.0);
        SocialClassRules.FoodDemandPer1KPops(SocialClass.Slaves).ShouldBe(1.0);
        SocialClassRules.FoodDemandPer1KPops(SocialClass.Tribesmen).ShouldBe(0.0);
    }

    [Fact]
    public void PopulationCapacity_HalvesInMountainsAndBoostsUrbanLocations()
    {
        var ruralFlat = new LocationRuntime { Rank = LocationRank.RuralSettlement, Topography = TopographyType.Flatland, Development = 10 };
        var ruralMtn = ruralFlat with { Topography = TopographyType.Mountains };
        var cityCoast = ruralFlat with { Rank = LocationRank.City, IsCoastal = true, IsCapital = true };

        var capFlat = DemographySystem.ComputePopulationCapacity(40.0, in ruralFlat);
        var capMtn = DemographySystem.ComputePopulationCapacity(40.0, in ruralMtn);
        var capCity = DemographySystem.ComputePopulationCapacity(40.0, in cityCoast);

        capMtn.ShouldBeLessThan(capFlat);
        Math.Abs(capMtn - capFlat * 0.5).ShouldBeLessThan(10.0);
        capCity.ShouldBeGreaterThan(capFlat);
    }
}
