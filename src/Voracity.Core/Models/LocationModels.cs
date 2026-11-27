using System.Text.Json.Serialization;

namespace Voracity.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocationKind : byte
{
    Habitable = 0,
    Impassable = 1,
    Corridor = 2,
    SeaZone = 3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocationRank : byte
{
    RuralSettlement = 0,
    Town = 1,
    City = 2,
    Megalopolis = 3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopographyType : byte
{
    Flatland = 0,
    Hills = 1,
    Mountains = 2,
    Plateau = 3,
    Wetlands = 4,
    CoastalOcean = 5,
    InlandSea = 6,
    Narrows = 7,
    Ocean = 8,
    DeepOcean = 9,
    Lake = 10
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VegetationType : byte
{
    Sparse = 0,
    Grasslands = 1,
    Farmland = 2,
    Woods = 3,
    Forest = 4,
    Jungle = 5,
    Desert = 6,
    Tundra = 7,
    Sea = 8
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClimateType : byte
{
    Oceanic = 0,
    Continental = 1,
    Mediterranean = 2,
    Arid = 3,
    Subarctic = 4,
    Arctic = 5,
    Tropical = 6,
    Maritime = 7
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationStatus : byte
{
    Colonized = 0,
    Conquered = 1,
    Integrated = 2,
    Core = 3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoadLevel : byte
{
    None = 0,
    GravelRoad = 1,
    PavedRoad = 2,
    ModernRoad = 3,
    Railroad = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RgoMethod : byte
{
    Farming = 0,
    Mining = 1,
    Logging = 2,
    Gathering = 3,
    Hunting = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocationEdgeKind : byte
{
    LandLand = 0,
    Strait = 1,
    CoastSea = 2,
    SeaSea = 3
}

public sealed record LocationEdgeInfo(
    LocationEdgeKind Kind,
    bool HasRiver = false,
    RoadLevel RoadLevel = RoadLevel.None,
    int StraitSeaZoneFaceId = 0);

public sealed record LocationDefinition(
    string Id,
    string NameKey,
    string DefaultName,
    LocationKind Kind,
    string ProvinceId,
    string AreaId,
    string RegionId,
    string SuperRegionId,
    string ContinentId,
    string? OwnerNationId,
    LocationRank Rank,
    TopographyType Topography,
    VegetationType Vegetation,
    ClimateType Climate,
    string? RawGoodId,
    RgoMethod RgoMethod,
    int NaturalHarborSuitability,
    double Development,
    RoadLevel RoadLevel,
    long InitialPopulation,
    float SeedX,
    float SeedY,
    double BasePopulationCapacity,
    bool IsCapital,
    bool IsMarketCenter,
    string? MarketId,
    double SeaZoneMaritimePresence = 0.0,
    int PersistentUid = 0);

public sealed record SpecialLocationDefinition(
    string Id,
    string Name,
    LocationKind Kind,
    TopographyType Topography,
    VegetationType Vegetation,
    ClimateType Climate,
    float SeedX,
    float SeedY,
    string[] ConnectedLocationIds,
    int PersistentUid = 0);

public sealed record SeaZoneDefinition(
    string Id,
    string Name,
    TopographyType Topography,
    float SeedX,
    float SeedY,
    double MaritimePresence = 0.50,
    int PersistentUid = 0);

public sealed record MarketDefinition(
    string Id,
    string NameKey,
    string CenterLocationId,
    string ColorHex,
    double BaseAttraction = 1.0);
