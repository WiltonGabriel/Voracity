using System.Text.Json.Serialization;

namespace Voracity.Core.Models;

public sealed record NationDefinition(
    string Id,
    string Name,
    string RankTier,
    string CapitalStateId,
    string PrimaryAncestryId,
    int AuthorityLevel,
    double StartingTreasuryGold,
    string MapColorHex,
    string RulerName,
    string RulerPrimaryAncestry,
    string? RulerSecondaryAncestry,
    string HeirName,
    string DynastyName = "Casa Soberana",
    string[]? RulerTraits = null,
    double StartingCourtSpendingPercent = 50.0,
    bool ProducesBloodEssence = false)
{
    public string[] EnsureRulerTraits() => RulerTraits ?? ["just", "diligent"];
}

public sealed record InitialPopSpec(
    string Profession,
    string PrimaryAncestry,
    string? SecondaryAncestry,
    long Population,
    double Sol,
    double Literacy);

public sealed record ProvinceDefinition(
    string Id,
    string Name,
    string Terrain,
    string HubType,
    bool IsCapital,
    [property: JsonIgnore] bool IsCoastal,
    double AnchorX,
    double AnchorY,
    int StartingFortLevel = 0,
    string? ResourcePotential = null,
    int PersistentUid = 0,
    LocationKind Kind = LocationKind.Habitable,
    LocationRank Rank = LocationRank.RuralSettlement,
    TopographyType Topography = TopographyType.Flatland,
    VegetationType Vegetation = VegetationType.Grasslands,
    ClimateType Climate = ClimateType.Continental,
    string? RawGoodId = null,
    RgoMethod RgoMethod = RgoMethod.Farming,
    int NaturalHarborSuitability = 20,
    double Development = 6.0,
    RoadLevel RoadLevel = RoadLevel.None,
    double BasePopulationCapacity = 42.0,
    string? MarketId = null);

public sealed record StateDefinition(
    string Id,
    string Name,
    string OwnerNationId,
    string MacroRegion,
    int ArableLand,
    double MapX,
    double MapY,
    double Radius,
    string[] AdjacentStateIds,
    string[] Provinces,
    InitialPopSpec[] InitialPops,
    ProvinceDefinition[]? ProvinceDetails = null,
    string? ContinentId = null,
    string? SuperRegionId = null,
    string? RegionId = null)
{
    public ProvinceDefinition[] EnsureProvinceDetails() => ProvinceDetails ?? Array.Empty<ProvinceDefinition>();
}

/// <summary>
/// Compact value-type struct stored contiguously in a buffer to avoid GC churn during EU5 ticks.
/// </summary>
public struct PopRecord
{
    public int LocationIndex;
    public int StateIndex;
    public int NationIndex;
    public SocialClass Class;
    public DualAncestry Ancestry;
    public long Population;
    public float Literacy;
    public float Satisfaction;
    public bool Active;
}

public readonly record struct GameDate(int Year, int Month, int Day, int SubTick6h)
{
    public GameDate AdvanceOneSubTick()
    {
        var nextSub = SubTick6h + 1;
        if (nextSub < 4)
            return new GameDate(Year, Month, Day, nextSub);

        var nextDay = Day + 1;
        if (nextDay <= 30)
            return new GameDate(Year, Month, nextDay, 0);

        var nextMonth = Month + 1;
        if (nextMonth <= 12)
            return new GameDate(Year, nextMonth, 1, 0);

        return new GameDate(Year + 1, 1, 1, 0);
    }

    public string ToDisplayString() => $"{Day:00}/{Month:00}/{Year} D.Q. ({SubTick6h * 6:00}:00)";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MapLensMode
{
    Political,
    Terrain,
    RgoAndMarket,
    ControlProximity,
    PopulationFood
}

public abstract record GameCommand;
public sealed record SetPausedCommand(bool Paused) : GameCommand;
public sealed record SetSpeedCommand(int Speed) : GameCommand;
public sealed record SetTaxRateCommand(string NationId, double TaxRate) : GameCommand;
public sealed record SetCourtSpendingCommand(string NationId, double CostOfCourtPercent) : GameCommand;
public sealed record BuildStructureCommand(string LocationId, string BuildingId, int Levels = 1) : GameCommand;
public sealed record SwitchProductionMethodCommand(string LocationId, string BuildingId, string PmId) : GameCommand;
public sealed record UpgradeLocationCommand(string LocationId, LocationUpgradeKind UpgradeKind) : GameCommand;

/// <summary>
/// Immutable snapshot emitted by SimulationWorld for the Godot 4 Vulkan client.
/// </summary>
public sealed record RenderSnapshot(
    GameDate Date,
    bool IsPaused,
    int Speed,
    RealmHeader Realm,
    MonetaryAmount Treasury,
    MonetaryAmount MonthlyBalance,
    double TaxRate,
    double TotalPotentialTaxBase,
    double TotalEffectiveTaxBase,
    double AverageControl,
    double TotalFoodBalance,
    int StarvingProvincesCount,
    long TotalWorldPopulation,
    long PlayerPopulation,
    int ActivePopCount,
    int ActiveAlertsCount,
    IReadOnlyDictionary<string, double> MarketPricesGold,
    IReadOnlyList<MarketSnapshot> Markets,
    IReadOnlyList<StateSnapshot> States,
    IReadOnlyList<ProvinceSnapshot> Provinces,
    IReadOnlyList<ProvinceFoodSnapshot> ProvinceFoods,
    IReadOnlyList<EstateSummarySnapshot> Estates,
    IReadOnlyList<PopSummarySnapshot> PlayerPops);

public sealed record StateSnapshot(
    string StateId,
    string Name,
    string OwnerNationId,
    string OwnerColorHex,
    float MapX,
    float MapY,
    float Radius,
    long Population,
    double AverageControl,
    double EffectiveTaxBase,
    string DominantAncestryColorHex,
    bool IsPlayerCapital,
    IReadOnlyList<string> AdjacentStateIds,
    IReadOnlyList<string> Provinces,
    IReadOnlyList<string> ProvinceIds,
    IReadOnlyList<string> ResourceTags,
    string? RegionId = null,
    string? SuperRegionId = null,
    string? LocalAdminTitle = null);

public sealed record ProvinceSnapshot(
    int NumericalIndex,
    string ProvinceId,
    string Name,
    string StateId,
    string OwnerNationId,
    string Terrain,
    string HubType,
    bool IsCapital,
    bool IsCoastal,
    float AnchorX,
    float AnchorY,
    int HoldingLevel,
    int FortLevel,
    string? ResourcePotential,
    string? OccupiedByNationId,
    string? LocalAdminTitle = null,
    string? DynamicDisplayName = null,
    LocationKind Kind = LocationKind.Habitable,
    LocationRank Rank = LocationRank.RuralSettlement,
    TopographyType Topography = TopographyType.Flatland,
    VegetationType Vegetation = VegetationType.Grasslands,
    ClimateType Climate = ClimateType.Continental,
    RgoMethod RgoMethod = RgoMethod.Farming,
    int NaturalHarborSuitability = 0,
    double Development = 0.0,
    RoadLevel RoadLevel = RoadLevel.None,
    IntegrationStatus IntegrationStatus = IntegrationStatus.Core,
    double Proximity = 0.0,
    double MaxControl = 0.0,
    double Control = 0.0,
    string? MarketId = null,
    double MarketAccess = 0.0,
    int RgoLevel = 0,
    int MaxRgoLevel = 0,
    double RgoEmploymentRatio = 0.0,
    double PotentialTaxBase = 0.0,
    double EffectiveTaxBase = 0.0,
    double LocalFoodProduced = 0.0,
    double LocalFoodConsumed = 0.0,
    long Population = 0,
    long PopulationCapacity = 0,
    double Prosperity = 0.0,
    double SeaZoneMaritimePresence = 0.0,
    IReadOnlyList<BuildingSlotSnapshot>? Buildings = null,
    bool IsMarketCenter = false);
