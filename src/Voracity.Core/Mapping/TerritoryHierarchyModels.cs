namespace Voracity.Core.Mapping;

/// <summary>
/// Normalized 2D coordinate ([0..1] in canonical Pontaria map space).
/// </summary>
public readonly record struct Point2D(float X, float Y);

/// <summary>
/// EU4-inspired immutable geographic hierarchy (Continent -> SuperRegion -> Region -> Area -> Province).
/// </summary>
public sealed record ContinentDefinition(
    string Id,
    string NameKey,
    string DefaultName,
    string[] SuperRegionIds);

public sealed record SuperRegionDefinition(
    string Id,
    string ContinentId,
    string NameKey,
    string DefaultName,
    string[] RegionIds);

public sealed record RegionDefinition(
    string Id,
    string SuperRegionId,
    string NameKey,
    string DefaultName,
    string Climate,
    string[] AreaIds);

public sealed record AreaDefinition(
    string Id,
    string RegionId,
    string NameKey,
    string DefaultName,
    string[] ProvinceIds);

public sealed record GeographyHierarchyData(
    ContinentDefinition[] Continents,
    SuperRegionDefinition[] SuperRegions,
    RegionDefinition[] Regions,
    AreaDefinition[] Areas)
{
    public static GeographyHierarchyData Empty { get; } = new([], [], [], []);
}

/// <summary>
/// Culturally and politically configurable administrative naming & dynamic toponyms.
/// Geographic hierarchy stays invariant in the simulation while administrative titles adapt to each culture/government.
/// </summary>
public sealed record DynamicProvinceNameRule(
    string ProvinceId,
    string CultureOrNationId,
    string LocalizedName);

public sealed record AdministrationProfile(
    string Id,
    string CultureTag,
    string? GovernmentArchetype,
    string SuperRegionTitle,
    string RegionTitle,
    string AreaTitle,
    string ProvinceTitle,
    string CapitalTitle,
    DynamicProvinceNameRule[]? DynamicProvinceNames = null);

/// <summary>
/// Runtime administrative subdivision when an Area/State is split between multiple sovereigns or renamed by ruler culture.
/// </summary>
public sealed record PolitySubdivision(
    string AreaId,
    string OwnerNationId,
    string DisplayTitle,
    string LocalAdminTierLabel,
    string[] ControlledProvinceIds);

/// <summary>
/// Natural geographic and strategic map features (editable in Map Editor and consumed by Bake + Client overlay).
/// </summary>
public sealed record LandmassContourDefinition(
    string Id,
    string Name,
    string ContinentId,
    string Kind,
    Point2D[] PolygonPoints);

public sealed record RiverFeature(
    string Id,
    string Name,
    Point2D[] ControlPoints,
    float Width = 1.8f,
    bool IsNavigable = false);

public sealed record MountainRangeFeature(
    string Id,
    string Name,
    Point2D[] SpinePoints,
    float PeakElevation = 0.92f,
    float Width = 0.032f,
    bool IsImpassable = false);

public sealed record LakeFeature(
    string Id,
    string Name,
    Point2D[] PolygonPoints);

public sealed record ForestFeature(
    string Id,
    string Name,
    Point2D[] PolygonPoints,
    float Density = 0.75f);

public sealed record StraitCrossing(
    string Id,
    string Name,
    string ProvinceA,
    string ProvinceB);

public sealed record MapBarrierFeature(
    string Id,
    string Name,
    string LabelKey,
    Point2D[] PolylinePoints);

public sealed record GeographyFeaturesData(
    LandmassContourDefinition[] Landmasses,
    RiverFeature[] Rivers,
    MountainRangeFeature[] MountainRanges,
    LakeFeature[] Lakes,
    ForestFeature[] Forests,
    StraitCrossing[] Straits,
    MapBarrierFeature[]? Barriers = null)
{
    public MapBarrierFeature[] EnsureBarriers() => Barriers ?? Array.Empty<MapBarrierFeature>();
    public static GeographyFeaturesData Empty { get; } = new([], [], [], [], [], [], []);
}

/// <summary>
/// Phase 6 3D-Readiness contract: separates normalized simulation space from metric 3D terrain & spline queries
/// so a future Godot 3D/MeshInstance3D terrain view can consume Core geometry cleanly without rewriting simulation logic.
/// </summary>
public interface IMapWorldGeometry
{
    float WorldWidthKm { get; }
    float WorldHeightKm { get; }
    float MaxElevationMeters { get; }

    (float XKm, float ZKm) NormalizedToWorldKm(float normX, float normY);
    (float NormX, float NormY) WorldKmToNormalized(float xKm, float zKm);
    float SampleElevationMeters(float normX, float normY);
    bool IsWaterAt(float normX, float normY);
    IReadOnlyList<(string RiverId, Point2D[] PointsKm)> GetRiverSplinesKm();
}
