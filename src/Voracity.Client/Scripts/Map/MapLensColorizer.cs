using Godot;
using Voracity.Core.Models;

namespace Voracity.Client.Map;

/// <summary>
/// Inspector-configurable color palette passed to MapLensColorizer with zero heap allocations.
/// </summary>
public readonly record struct MapLensPaletteConfig(
    Color ControlLow,
    Color ControlMid,
    Color ControlHigh,
    Color PopulationLow,
    Color PopulationHigh,
    Color Starving,
    Color SeaZone,
    Color MaritimeHigh,
    Color Corridor,
    Color Impassable)
{
    public static MapLensPaletteConfig Default { get; } = new(
        ControlLow: new Color(0.76f, 0.22f, 0.18f),
        ControlMid: new Color(0.88f, 0.72f, 0.24f),
        ControlHigh: new Color(0.22f, 0.72f, 0.38f),
        PopulationLow: new Color(0.26f, 0.34f, 0.42f),
        PopulationHigh: new Color(0.32f, 0.78f, 0.42f),
        Starving: new Color(0.78f, 0.24f, 0.20f),
        SeaZone: new Color(0.11f, 0.20f, 0.27f, 1.0f),
        MaritimeHigh: new Color(0.22f, 0.62f, 0.76f, 1.0f),
        Corridor: new Color(0.56f, 0.50f, 0.36f),
        Impassable: new Color(0.14f, 0.13f, 0.16f, 1.0f));
}

/// <summary>
/// Computes GPU lookup colors for the 5 EU5 MapLensModes plus LocationKind encoding in row-2 alpha.
/// </summary>
public static class MapLensColorizer
{
    public static Color ResolveLensColor(
        ProvinceSnapshot loc,
        StateSnapshot? state,
        MapLensMode lens,
        IReadOnlyDictionary<string, Color> marketColors,
        in MapLensPaletteConfig palette)
    {
        if (loc.Kind == LocationKind.Impassable)
            return palette.Impassable;

        if (loc.Kind == LocationKind.SeaZone)
        {
            return lens == MapLensMode.ControlProximity
                ? palette.SeaZone.Lerp(palette.MaritimeHigh, Mathf.Clamp((float)loc.SeaZoneMaritimePresence, 0f, 1f))
                : palette.SeaZone;
        }

        var baseColor = lens switch
        {
            MapLensMode.Political => ResolvePoliticalColor(loc, state, palette),
            MapLensMode.Terrain => ResolveTerrainPreviewColor(loc.Topography, loc.Vegetation),
            MapLensMode.RgoAndMarket => ResolveMarketOrRgoColor(loc, marketColors),
            MapLensMode.ControlProximity => LerpControlColor(loc.Control, palette),
            MapLensMode.PopulationFood => LerpPopulationFoodColor(loc.Population, Math.Max(1L, loc.PopulationCapacity), loc.LocalFoodProduced - loc.LocalFoodConsumed, palette),
            _ => ResolvePoliticalColor(loc, state, palette)
        };

        var mod = 0.94f + ((loc.NumericalIndex % 5) - 2) * 0.025f;
        return new Color(
            Mathf.Clamp(baseColor.R * mod, 0.05f, 0.96f),
            Mathf.Clamp(baseColor.G * mod, 0.05f, 0.96f),
            Mathf.Clamp(baseColor.B * mod, 0.05f, 0.96f),
            1.0f);
    }

    public static float EncodeLocationKindAlpha(LocationKind kind) => kind switch
    {
        LocationKind.Habitable => 0.0f,
        LocationKind.Impassable => 85.0f / 255.0f,
        LocationKind.Corridor => 170.0f / 255.0f,
        LocationKind.SeaZone => 1.0f,
        _ => 0.0f
    };

    private static Color ResolvePoliticalColor(ProvinceSnapshot loc, StateSnapshot? state, in MapLensPaletteConfig palette)
    {
        if (loc.Kind == LocationKind.Corridor)
            return palette.Corridor;
        return state is not null ? Color.FromHtml(state.OwnerColorHex) : new Color(0.45f, 0.45f, 0.42f);
    }

    private static Color ResolveMarketOrRgoColor(ProvinceSnapshot loc, IReadOnlyDictionary<string, Color> marketColors)
    {
        if (loc.MarketId is not null && marketColors.TryGetValue(loc.MarketId, out var mktCol))
        {
            var access = (float)Math.Clamp(loc.MarketAccess, 0.20, 1.0);
            return mktCol.Darkened(0.45f * (1f - access));
        }
        return new Color(0.44f, 0.44f, 0.40f);
    }

    private static Color LerpControlColor(double control, in MapLensPaletteConfig palette)
    {
        var t = Mathf.Clamp((float)(control / 100.0), 0f, 1f);
        return t < 0.5f
            ? palette.ControlLow.Lerp(palette.ControlMid, t * 2f)
            : palette.ControlMid.Lerp(palette.ControlHigh, (t - 0.5f) * 2f);
    }

    private static Color LerpPopulationFoodColor(long pop, long cap, double netFood, in MapLensPaletteConfig palette)
    {
        if (netFood < -5.0)
            return palette.Starving;
        var ratio = Mathf.Clamp((float)pop / cap, 0f, 1f);
        return palette.PopulationLow.Lerp(palette.PopulationHigh, ratio);
    }

    private static Color ResolveTerrainPreviewColor(TopographyType topo, VegetationType veg) => (topo, veg) switch
    {
        (TopographyType.Mountains, _) => new Color(0.42f, 0.41f, 0.45f),
        (TopographyType.Hills, _) => new Color(0.50f, 0.45f, 0.31f),
        (TopographyType.Wetlands, _) => new Color(0.24f, 0.38f, 0.33f),
        (_, VegetationType.Farmland) => new Color(0.42f, 0.56f, 0.28f),
        (_, VegetationType.Forest or VegetationType.Woods or VegetationType.Jungle) => new Color(0.18f, 0.35f, 0.22f),
        (_, VegetationType.Desert or VegetationType.Sparse) => new Color(0.68f, 0.56f, 0.36f),
        _ => new Color(0.42f, 0.48f, 0.30f)
    };
}
