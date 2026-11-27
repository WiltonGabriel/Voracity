using System.Text.Json.Serialization;

namespace Voracity.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SocialClass : byte
{
    Nobles = 0,
    Clerics = 1,
    Burghers = 2,
    Laborers = 3,
    Soldiers = 4,
    Peasants = 5,
    Tribesmen = 6,
    Slaves = 7
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EstateKind : byte
{
    Crown = 0,
    Nobles = 1,
    Clergy = 2,
    Burghers = 3,
    Commoners = 4,
    Tribes = 5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocationUpgradeKind : byte
{
    ExpandRgo = 0,
    UpgradeRank = 1,
    UpgradeRoad = 2
}

public static class SocialClassRules
{
    public static double FoodDemandPer1KPops(SocialClass socialClass) => socialClass switch
    {
        SocialClass.Nobles => 20.0,
        SocialClass.Clerics => 5.0,
        SocialClass.Burghers => 4.0,
        SocialClass.Soldiers => 5.0,
        SocialClass.Laborers => 1.0,
        SocialClass.Peasants => 1.0,
        SocialClass.Slaves => 1.0,
        SocialClass.Tribesmen => 0.0,
        _ => 1.0
    };

    public static EstateKind ToEstate(SocialClass socialClass) => socialClass switch
    {
        SocialClass.Nobles => EstateKind.Nobles,
        SocialClass.Clerics => EstateKind.Clergy,
        SocialClass.Burghers => EstateKind.Burghers,
        SocialClass.Tribesmen => EstateKind.Tribes,
        _ => EstateKind.Commoners
    };

    public static double EstateTaxWeight(EstateKind estate) => estate switch
    {
        EstateKind.Nobles => 100.0,
        EstateKind.Clergy => 25.0,
        EstateKind.Burghers => 40.0,
        EstateKind.Commoners => 1.0,
        EstateKind.Tribes => 0.01,
        _ => 1.0
    };

    public static SocialClass ParseSocialClass(string raw) => raw.ToLowerInvariant() switch
    {
        "aristocrats" or "nobles" => SocialClass.Nobles,
        "clergy" or "clerics" => SocialClass.Clerics,
        "capitalists" or "clerks" or "academics" or "bureaucrats" or "burghers" => SocialClass.Burghers,
        "laborers" or "machinists" or "engineers" or "artisans" => SocialClass.Laborers,
        "soldiers" or "officers" or "adventurers" => SocialClass.Soldiers,
        "peasants" => SocialClass.Peasants,
        "tribesmen" => SocialClass.Tribesmen,
        "slaves" or "thralls" or "serfs" => SocialClass.Slaves,
        _ => SocialClass.Peasants
    };
}

/// <summary>
/// Clean v0.1 hook exposing Estate Tax Base Shares and future v0.2 political extensions.
/// </summary>
public interface IEstateTaxHook
{
    double PeasantEnfranchisement { get; }
    double GetWeight(EstateKind estate);
}

public sealed class DefaultEstateTaxHook : IEstateTaxHook
{
    public double PeasantEnfranchisement => 0.50;
    public double GetWeight(EstateKind estate) => SocialClassRules.EstateTaxWeight(estate);
}

public sealed record RealmHeader(
    string NationId,
    string NationName,
    string DynastyName,
    string RulerName,
    string HeirName,
    string RulerAncestryDisplay,
    IReadOnlyList<string> RulerTraits,
    double GovernmentPower,
    double Stability,
    double Prestige,
    double CostOfCourtPercent);

public sealed record EstateSummarySnapshot(
    EstateKind Estate,
    double TaxBaseSharePercent,
    double EffectiveTaxRate,
    MonetaryAmount MonthlyTaxYield);

public sealed record MarketGoodSnapshot(
    string GoodId,
    double CurrentPriceGold,
    double BasePriceGold,
    double Supply,
    double Demand,
    double FoodValue);

public sealed record MarketSnapshot(
    string MarketId,
    string NameKey,
    string CenterLocationId,
    string ColorHex,
    int LocationCount,
    double AverageAccess,
    IReadOnlyList<MarketGoodSnapshot> Goods);

public sealed record ProvinceFoodSnapshot(
    string ProvinceId,
    string Name,
    string AreaId,
    double FoodStorage,
    double MaxFoodStorage,
    double NetMonthlyFood,
    bool IsStarving);

public sealed record SimulationIntegrityReport(
    GameDate Date,
    int TotalTicksExecuted,
    int NationsCount,
    int LocationsCount,
    int MarketsCount,
    int ActivePopCount,
    long TotalWorldPopulation,
    long TotalTreasuryCoppers,
    double MinPriceRatio,
    double MaxPriceRatio,
    double AverageControl,
    int StarvingProvincesCount,
    bool IsHealthy,
    IReadOnlyList<string> Anomalies);
