using System.Text.Json;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Core.Data;

public sealed record ModManifest(
    string Id,
    string Name,
    string Version,
    int LoadOrder,
    bool Enabled);

public sealed record WorldDatabase(
    IReadOnlyList<AncestryDefinition> Ancestries,
    IReadOnlyList<GoodDefinition> Goods,
    IReadOnlyList<NationDefinition> Nations,
    IReadOnlyList<StateDefinition> States,
    IReadOnlyList<BuildingDefinition> Buildings,
    LocalizationCatalog Localization,
    IReadOnlyList<ModManifest> LoadedMods)
{
    public GeographyHierarchyData GeographyHierarchy { get; init; } = GeographyHierarchyData.Empty;
    public GeographyFeaturesData GeographyFeatures { get; init; } = GeographyFeaturesData.Empty;
    public IReadOnlyList<AdministrationProfile> AdminProfiles { get; init; } = Array.Empty<AdministrationProfile>();
    public IReadOnlyList<DynamicProvinceNameRule> DynamicProvinceNames { get; init; } = Array.Empty<DynamicProvinceNameRule>();
    public IReadOnlyList<SeaZoneDefinition> SeaZones { get; init; } = Array.Empty<SeaZoneDefinition>();
    public IReadOnlyList<SpecialLocationDefinition> SpecialLocations { get; init; } = Array.Empty<SpecialLocationDefinition>();
    public IReadOnlyList<MarketDefinition> Markets { get; init; } = Array.Empty<MarketDefinition>();

    public IReadOnlyList<LocationDefinition> BuildAllLocations()
    {
        var areaToRegion = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in GeographyHierarchy.Areas)
            areaToRegion[a.Id] = a.RegionId;

        var regionToSuper = new Dictionary<string, (string SuperRegionId, string ContinentId)>(StringComparer.Ordinal);
        var srToContinent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sr in GeographyHierarchy.SuperRegions)
            srToContinent[sr.Id] = sr.ContinentId;
        foreach (var r in GeographyHierarchy.Regions)
            regionToSuper[r.Id] = (r.SuperRegionId, srToContinent.GetValueOrDefault(r.SuperRegionId, "pontaria"));

        var marketCenters = new HashSet<string>(Markets.Select(m => m.CenterLocationId), StringComparer.Ordinal);
        var list = new List<LocationDefinition>(States.Sum(s => s.EnsureProvinceDetails().Length) + SpecialLocations.Count + SeaZones.Count);

        foreach (var st in States)
        {
            var regionId = st.RegionId ?? areaToRegion.GetValueOrDefault(st.Id, string.Empty);
            var (srId, contId) = regionToSuper.TryGetValue(regionId, out var pair)
                ? pair
                : (st.SuperRegionId ?? string.Empty, st.ContinentId ?? "pontaria");

            var details = st.EnsureProvinceDetails();
            var popSum = 0L;
            foreach (var p in st.InitialPops)
                popSum += p.Population;
            var perLocPop = details.Length > 0 ? Math.Max(1000L, popSum / details.Length) : 1000L;

            foreach (var p in details)
            {
                list.Add(new LocationDefinition(
                    Id: p.Id,
                    NameKey: $"loc.{p.Id}",
                    DefaultName: p.Name,
                    Kind: p.Kind,
                    ProvinceId: p.Id,
                    AreaId: st.Id,
                    RegionId: regionId,
                    SuperRegionId: srId,
                    ContinentId: contId,
                    OwnerNationId: st.OwnerNationId,
                    Rank: p.Rank,
                    Topography: p.Topography,
                    Vegetation: p.Vegetation,
                    Climate: p.Climate,
                    RawGoodId: p.RawGoodId ?? p.ResourcePotential ?? "grain",
                    RgoMethod: p.RgoMethod,
                    NaturalHarborSuitability: p.NaturalHarborSuitability,
                    Development: p.Development,
                    RoadLevel: p.RoadLevel,
                    InitialPopulation: perLocPop,
                    SeedX: (float)p.AnchorX,
                    SeedY: (float)p.AnchorY,
                    BasePopulationCapacity: p.BasePopulationCapacity,
                    IsCapital: p.IsCapital,
                    IsMarketCenter: marketCenters.Contains(p.Id),
                    MarketId: p.MarketId,
                    SeaZoneMaritimePresence: 0.0,
                    PersistentUid: p.PersistentUid));
            }
        }

        foreach (var sp in SpecialLocations)
        {
            list.Add(new LocationDefinition(
                Id: sp.Id,
                NameKey: $"loc.{sp.Id}",
                DefaultName: sp.Name,
                Kind: sp.Kind,
                ProvinceId: sp.Id,
                AreaId: string.Empty,
                RegionId: string.Empty,
                SuperRegionId: string.Empty,
                ContinentId: "pontaria",
                OwnerNationId: null,
                Rank: LocationRank.RuralSettlement,
                Topography: sp.Topography,
                Vegetation: sp.Vegetation,
                Climate: sp.Climate,
                RawGoodId: null,
                RgoMethod: RgoMethod.Gathering,
                NaturalHarborSuitability: 0,
                Development: 0.0,
                RoadLevel: RoadLevel.None,
                InitialPopulation: 0,
                SeedX: sp.SeedX,
                SeedY: sp.SeedY,
                BasePopulationCapacity: 0.0,
                IsCapital: false,
                IsMarketCenter: false,
                MarketId: null,
                SeaZoneMaritimePresence: 0.0,
                PersistentUid: sp.PersistentUid));
        }

        foreach (var sz in SeaZones)
        {
            list.Add(new LocationDefinition(
                Id: sz.Id,
                NameKey: $"loc.{sz.Id}",
                DefaultName: sz.Name,
                Kind: LocationKind.SeaZone,
                ProvinceId: sz.Id,
                AreaId: string.Empty,
                RegionId: string.Empty,
                SuperRegionId: string.Empty,
                ContinentId: "pontaria",
                OwnerNationId: null,
                Rank: LocationRank.RuralSettlement,
                Topography: sz.Topography,
                Vegetation: VegetationType.Sea,
                Climate: ClimateType.Maritime,
                RawGoodId: null,
                RgoMethod: RgoMethod.Gathering,
                NaturalHarborSuitability: 0,
                Development: 0.0,
                RoadLevel: RoadLevel.None,
                InitialPopulation: 0,
                SeedX: sz.SeedX,
                SeedY: sz.SeedY,
                BasePopulationCapacity: 0.0,
                IsCapital: false,
                IsMarketCenter: false,
                MarketId: null,
                SeaZoneMaritimePresence: sz.MaritimePresence,
                PersistentUid: sz.PersistentUid));
        }

        return list;
    }
}

/// <summary>
/// OpenVic / Paradox Jomini-inspired declarative data loader.
/// Loads data/base/ first, then overlays enabled mods in data/mods/ sorted by LoadOrder.
/// </summary>
public static class ModdingDataLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static WorldDatabase Load(string dataRootPath)
    {
        var baseDir = Path.Combine(dataRootPath, "base");
        if (!Directory.Exists(baseDir))
            throw new DirectoryNotFoundException($"Base data directory not found: {baseDir}");

        var ancestries = ReadJsonArray<AncestryDefinition>(Path.Combine(baseDir, "ancestries.json"));
        var goods = ReadJsonArray<GoodDefinition>(Path.Combine(baseDir, "goods.json"));
        var nations = ReadJsonArray<NationDefinition>(Path.Combine(baseDir, "nations.json"));
        var states = ReadJsonArray<StateDefinition>(Path.Combine(baseDir, "states_provinces.json"));
        var buildings = ReadOptionalJsonArray<BuildingDefinition>(Path.Combine(baseDir, "buildings.json"));
        var seaZones = ReadOptionalJsonArray<SeaZoneDefinition>(Path.Combine(baseDir, "sea_zones.json"));
        var specialLocations = ReadOptionalJsonArray<SpecialLocationDefinition>(Path.Combine(baseDir, "special_locations.json"));
        var markets = ReadOptionalJsonArray<MarketDefinition>(Path.Combine(baseDir, "markets.json"));

        var geoHierarchy = ReadOptionalJsonObject<GeographyHierarchyData>(Path.Combine(baseDir, "geography_hierarchy.json"))
            ?? GeographyHierarchyData.Empty;
        var geoFeatures = ReadOptionalJsonObject<GeographyFeaturesData>(Path.Combine(baseDir, "geography_features.json"))
            ?? GeographyFeaturesData.Empty;
        var adminPayload = ReadOptionalJsonObject<AtlasVersioning.AdminProfilesFilePayload>(Path.Combine(baseDir, "admin_profiles.json"));
        var adminProfiles = adminPayload?.Profiles.ToList() ?? new List<AdministrationProfile>();
        var dynamicNames = adminPayload?.DynamicProvinceNames.ToList() ?? new List<DynamicProvinceNameRule>();

        var locTables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        LoadLocalizationDir(Path.Combine(baseDir, "localization"), locTables);

        var loadedMods = new List<ModManifest>();
        var modsRoot = Path.Combine(dataRootPath, "mods");
        if (Directory.Exists(modsRoot))
        {
            var discovered = new List<(ModManifest Manifest, string Dir)>();
            foreach (var dir in Directory.GetDirectories(modsRoot))
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath))
                    continue;

                var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is { Enabled: true })
                    discovered.Add((manifest, dir));
            }

            foreach (var (manifest, dir) in discovered.OrderBy(m => m.Manifest.LoadOrder).ThenBy(m => m.Manifest.Id, StringComparer.Ordinal))
            {
                MergeById(ancestries, Path.Combine(dir, "ancestries.json"), x => x.Id);
                MergeById(goods, Path.Combine(dir, "goods.json"), x => x.Id);
                MergeById(nations, Path.Combine(dir, "nations.json"), x => x.Id);
                MergeById(states, Path.Combine(dir, "states_provinces.json"), x => x.Id);
                MergeById(buildings, Path.Combine(dir, "buildings.json"), x => x.Id);
                MergeById(seaZones, Path.Combine(dir, "sea_zones.json"), x => x.Id);
                MergeById(specialLocations, Path.Combine(dir, "special_locations.json"), x => x.Id);
                MergeById(markets, Path.Combine(dir, "markets.json"), x => x.Id);

                var modGeoHierarchy = ReadOptionalJsonObject<GeographyHierarchyData>(Path.Combine(dir, "geography_hierarchy.json"));
                if (modGeoHierarchy is not null)
                    geoHierarchy = MergeHierarchy(geoHierarchy, modGeoHierarchy);

                var modFeatures = ReadOptionalJsonObject<GeographyFeaturesData>(Path.Combine(dir, "geography_features.json"));
                if (modFeatures is not null)
                    geoFeatures = MergeFeatures(geoFeatures, modFeatures);

                var modAdmin = ReadOptionalJsonObject<AtlasVersioning.AdminProfilesFilePayload>(Path.Combine(dir, "admin_profiles.json"));
                if (modAdmin is not null)
                {
                    MergeItemsById(adminProfiles, modAdmin.Profiles, x => x.Id);
                    MergeItemsById(dynamicNames, modAdmin.DynamicProvinceNames, x => $"{x.ProvinceId}|{x.CultureOrNationId}");
                }

                LoadLocalizationDir(Path.Combine(dir, "localization"), locTables);
                loadedMods.Add(manifest);
            }
        }

        return new WorldDatabase(
            ancestries,
            goods,
            nations,
            EnsureProvinceDetails(states),
            buildings,
            new LocalizationCatalog(locTables),
            loadedMods)
        {
            GeographyHierarchy = geoHierarchy,
            GeographyFeatures = geoFeatures,
            AdminProfiles = adminProfiles,
            DynamicProvinceNames = dynamicNames,
            SeaZones = seaZones,
            SpecialLocations = specialLocations,
            Markets = markets
        };
    }

    private static List<StateDefinition> EnsureProvinceDetails(List<StateDefinition> states)
    {
        for (var i = 0; i < states.Count; i++)
        {
            var st = states[i];
            if (st.ProvinceDetails is { Length: > 0 })
                continue;

            var count = Math.Max(1, st.Provinces.Length);
            var generated = new ProvinceDefinition[count];
            for (var p = 0; p < count; p++)
            {
                var angle = p * (2.0 * Math.PI / count);
                var dist = p == 0 ? 0.0 : Math.Max(0.025, st.Radius * 0.45);
                var ax = Math.Clamp(st.MapX + Math.Cos(angle) * dist, 0.08, 0.92);
                var ay = Math.Clamp(st.MapY + Math.Sin(angle) * dist * 0.85, 0.10, 0.88);
                var pName = p < st.Provinces.Length ? st.Provinces[p] : $"{st.Name} {p + 1}";
                generated[p] = new ProvinceDefinition(
                    Id: $"{st.Id}_p{p + 1}",
                    Name: pName,
                    Terrain: st.MacroRegion == "AridBelt" ? "desert" : (st.MacroRegion == "UndergroundSouth" ? "mountains" : "plains"),
                    HubType: p == 0 ? "castle" : (p % 3 == 1 ? "farm" : (p % 3 == 2 ? "mine" : "city")),
                    IsCapital: p == 0,
                    IsCoastal: false,
                    AnchorX: ax,
                    AnchorY: ay,
                    StartingFortLevel: p == 0 ? 1 : 0);
            }
            states[i] = st with { ProvinceDetails = generated };
        }
        return states;
    }

    private static List<T> ReadJsonArray<T>(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<List<T>>(json, JsonOptions)
            ?? throw new InvalidDataException($"Failed to deserialize array from {filePath}");
    }

    private static List<T> ReadOptionalJsonArray<T>(string filePath) =>
        File.Exists(filePath) ? ReadJsonArray<T>(filePath) : [];

    private static T? ReadOptionalJsonObject<T>(string filePath) where T : class
    {
        if (!File.Exists(filePath))
            return null;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(filePath), JsonOptions);
    }

    private static GeographyHierarchyData MergeHierarchy(GeographyHierarchyData baseData, GeographyHierarchyData mod)
    {
        var conts = baseData.Continents.ToList();
        var srs = baseData.SuperRegions.ToList();
        var regs = baseData.Regions.ToList();
        var areas = baseData.Areas.ToList();

        MergeItemsById(conts, mod.Continents, x => x.Id);
        MergeItemsById(srs, mod.SuperRegions, x => x.Id);
        MergeItemsById(regs, mod.Regions, x => x.Id);
        MergeItemsById(areas, mod.Areas, x => x.Id);

        return new GeographyHierarchyData(conts.ToArray(), srs.ToArray(), regs.ToArray(), areas.ToArray());
    }

    private static GeographyFeaturesData MergeFeatures(GeographyFeaturesData baseData, GeographyFeaturesData mod)
    {
        var land = baseData.Landmasses.ToList();
        var riv = baseData.Rivers.ToList();
        var mtn = baseData.MountainRanges.ToList();
        var lake = baseData.Lakes.ToList();
        var forest = baseData.Forests.ToList();
        var strait = baseData.Straits.ToList();
        var barriers = baseData.EnsureBarriers().ToList();

        MergeItemsById(land, mod.Landmasses, x => x.Id);
        MergeItemsById(riv, mod.Rivers, x => x.Id);
        MergeItemsById(mtn, mod.MountainRanges, x => x.Id);
        MergeItemsById(lake, mod.Lakes, x => x.Id);
        MergeItemsById(forest, mod.Forests, x => x.Id);
        MergeItemsById(strait, mod.Straits, x => x.Id);
        MergeItemsById(barriers, mod.EnsureBarriers(), x => x.Id);

        return new GeographyFeaturesData(
            land.ToArray(), riv.ToArray(), mtn.ToArray(), lake.ToArray(), forest.ToArray(), strait.ToArray(), barriers.ToArray());
    }

    private static void MergeItemsById<T>(List<T> target, IEnumerable<T> overrides, Func<T, string> idSelector)
    {
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < target.Count; i++)
            indexById[idSelector(target[i])] = i;

        foreach (var item in overrides)
        {
            var id = idSelector(item);
            if (indexById.TryGetValue(id, out var existingIndex))
                target[existingIndex] = item;
            else
            {
                indexById[id] = target.Count;
                target.Add(item);
            }
        }
    }

    private static void LoadLocalizationDir(string locDir, Dictionary<string, Dictionary<string, string>> targetTables)
    {
        if (!Directory.Exists(locDir))
            return;

        foreach (var file in Directory.GetFiles(locDir, "*.json"))
        {
            var langCode = Path.GetFileNameWithoutExtension(file);
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file), JsonOptions);
            if (entries is null)
                continue;

            if (!targetTables.TryGetValue(langCode, out var table))
            {
                table = new Dictionary<string, string>(StringComparer.Ordinal);
                targetTables[langCode] = table;
            }

            foreach (var (k, v) in entries)
                table[k] = v;
        }
    }

    private static void MergeById<T>(List<T> target, string overrideFile, Func<T, string> idSelector)
    {
        if (!File.Exists(overrideFile))
            return;

        var overrides = ReadJsonArray<T>(overrideFile);
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < target.Count; i++)
            indexById[idSelector(target[i])] = i;

        foreach (var item in overrides)
        {
            var id = idSelector(item);
            if (indexById.TryGetValue(id, out var existingIndex))
                target[existingIndex] = item;
            else
            {
                indexById[id] = target.Count;
                target.Add(item);
            }
        }
    }
}
