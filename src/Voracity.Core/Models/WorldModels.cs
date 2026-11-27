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
    string HeirName);

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
/// Compact value-type struct stored contiguously in a buffer to avoid GC churn during ticks.
/// </summary>
public struct PopRecord
{
    public int StateIndex;
    public int NationIndex;
    public int ProfessionIndex;
    public DualAncestry Ancestry;
    public long Population;
    public float StandardOfLiving;
    public float Literacy;
    public float Radicalism;
    public bool Active;
}

/// <summary>
/// Contiguous struct holding live per-province holding/fortification/occupation state without Godot Nodes.
/// </summary>
public struct ProvinceSlot
{
    public int ProvinceIndex;
    public int StateIndex;
    public int HoldingLevel;
    public int FortLevel;
    public int OccupiedByNationIndex;
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

public enum MapLensMode
{
    Political,
    Economic,
    Ancestry,
    StandardOfLiving,
    Diplomacy
}

public enum CourtActionKind
{
    RoyalBanquet,
    ResearchGrant,
    RaiseAuthority,
    LowerAuthority
}

public enum DiplomaticActionKind
{
    ImproveRelations,
    ToggleTradePact,
    ToggleAlliance,
    DeclareWar,
    MakePeace
}

public abstract record GameCommand;
public sealed record SetPausedCommand(bool Paused) : GameCommand;
public sealed record SetSpeedCommand(int Speed) : GameCommand;
public sealed record SetTaxRateCommand(string NationId, double TaxRate) : GameCommand;
public sealed record InvestStateFarmCommand(string StateId, int Levels) : GameCommand;
public sealed record BuildStructureCommand(string StateId, string BuildingId, int Levels = 1) : GameCommand;
public sealed record UpgradeProvinceHoldingCommand(string ProvinceId, string UpgradeKind) : GameCommand;
public sealed record SwitchProductionMethodCommand(string StateId, string BuildingId, string PmId) : GameCommand;
public sealed record EnactLawCommand(string NationId, string LawId) : GameCommand;
public sealed record StartResearchCommand(string NationId, string TechId) : GameCommand;
public sealed record CourtActionCommand(string NationId, CourtActionKind Action) : GameCommand;
public sealed record DiplomaticActionCommand(string ActorNationId, string TargetNationId, DiplomaticActionKind Action) : GameCommand;

/// <summary>
/// Immutable snapshot emitted by SimulationWorld for the Godot 4 Vulkan client.
/// </summary>
public sealed record RenderSnapshot(
    GameDate Date,
    bool IsPaused,
    int Speed,
    string PlayerNationId,
    string PlayerNationName,
    MonetaryAmount Treasury,
    MonetaryAmount WeeklyBalance,
    double TaxRate,
    int BureaucracyBalance,
    int AuthorityLevel,
    int RulerStress,
    string RulerName,
    string RulerAncestryDisplay,
    string HeirName,
    long TotalWorldPopulation,
    long PlayerPopulation,
    int ActivePopCount,
    int ActiveAlertsCount,
    string? ActiveLawId,
    double ActiveLawProgress,
    string? ActiveTechId,
    double ActiveTechProgress,
    IReadOnlyDictionary<string, double> MarketPricesGold,
    IReadOnlyList<StateSnapshot> States,
    IReadOnlyList<ProvinceSnapshot> Provinces,
    IReadOnlyList<InterestGroupSnapshot> PlayerInterestGroups,
    IReadOnlyList<LawStatusSnapshot> PlayerLaws,
    IReadOnlyList<TechnologyProgressSnapshot> PlayerTechnologies,
    IReadOnlyList<DiplomaticRelationSnapshot> PlayerDiplomacy,
    IReadOnlyList<JournalEntrySnapshot> PlayerJournalEntries,
    IReadOnlyList<PopSummarySnapshot> PlayerPops);

public sealed record StateSnapshot(
    string StateId,
    string Name,
    string OwnerNationId,
    string OwnerColorHex,
    float MapX,
    float MapY,
    float Radius,
    int ArableLand,
    long Population,
    float AverageSoL,
    double AveragePriceRatio,
    string DominantAncestryColorHex,
    int FarmLevels,
    double DiplomacyScoreWithPlayer,
    bool IsAtWarWithPlayer,
    bool HasTradePactWithPlayer,
    bool IsAlliedWithPlayer,
    bool IsPlayerCapital,
    IReadOnlyList<string> AdjacentStateIds,
    IReadOnlyList<string> Provinces,
    IReadOnlyList<string> ProvinceIds,
    IReadOnlyList<string> ResourceTags,
    IReadOnlyList<BuildingSnapshot> Buildings,
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
    ClimateType Climate = ClimateType.Continental);

public sealed record SimulationIntegrityReport(
    GameDate Date,
    int TotalTicksExecuted,
    int NationsCount,
    int ProvincesCount,
    int ActivePopsCount,
    long TotalWorldPopulation,
    long TotalTreasuryCoppers,
    double MinPriceRatio,
    double MaxPriceRatio,
    int CompletedTechCountTotal,
    int ActiveWarsCount,
    bool IsHealthy,
    IReadOnlyList<string> Anomalies);
