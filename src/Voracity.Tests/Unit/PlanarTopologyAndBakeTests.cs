using System.Diagnostics;
using Shouldly;
using Voracity.Core.Data;
using Voracity.Core.Mapping;
using Voracity.Core.Models;
using Voracity.Tests.Integration;

namespace Voracity.Tests.Unit;

public sealed class PlanarTopologyAndBakeTests
{
    [Fact]
    public void PlanarTopologyBuilder_EnforcesSymmetricAdjacenciesAllLocationKindsAndWatertightArcs()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var atlas = new EditableWorldAtlas(db);
        var topo = atlas.Topology;

        topo.Faces.Count(f => f.Kind == LocationKind.Habitable).ShouldBe(134);
        topo.Faces.Count(f => f.Kind == LocationKind.SeaZone).ShouldBeGreaterThanOrEqualTo(25);
        topo.Faces.Count(f => f.Kind == LocationKind.Corridor).ShouldBe(3);
        topo.Faces.Count(f => f.Kind == LocationKind.Impassable).ShouldBe(3);
        topo.Vertices.ShouldNotBeEmpty();
        topo.Arcs.ShouldNotBeEmpty();
        topo.AdjacencyGraph.VertexCount.ShouldBe(topo.Faces.Count);

        var adjPairs = new HashSet<(int A, int B)>();
        foreach (var (a, b) in topo.SymmetricProvinceAdjacencies)
        {
            a.ShouldNotBe(b);
            topo.FaceById.ContainsKey(a).ShouldBeTrue();
            topo.FaceById.ContainsKey(b).ShouldBeTrue();
            adjPairs.Add((a, b));
        }

        foreach (var arc in topo.Arcs)
        {
            arc.Points.Length.ShouldBeGreaterThanOrEqualTo(2);
            if (arc.LeftFaceId > 0 && arc.RightFaceId > 0)
            {
                var pair = arc.LeftFaceId < arc.RightFaceId
                    ? (arc.LeftFaceId, arc.RightFaceId)
                    : (arc.RightFaceId, arc.LeftFaceId);
                adjPairs.ShouldContain(pair);
            }
        }

        var inlandFace = topo.FaceByProvinceId["prov_briggston"];
        inlandFace.IsCoastal.ShouldBeFalse();
        topo.HitTestFace(inlandFace.SeedX, inlandFace.SeedY).ShouldBe(inlandFace.FaceId);
    }

    [Fact]
    public void MapBakePipeline_BakeAndVmapCacheRoundtrip_PreservesLocationGridsAndHashes()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var atlas = new EditableWorldAtlas(db);
        var snapshots = atlas.BuildProvinceSnapshots();

        var artifact = MapBakePipeline.BakeFromTopology(
            atlas.Topology,
            snapshots,
            db.GeographyFeatures,
            width: 360,
            height: 210);

        artifact.LocationIdGrid.Length.ShouldBe(360 * 210);
        artifact.LocationKindGrid.Length.ShouldBe(360 * 210);
        artifact.HeightCoastRgb.Length.ShouldBe(360 * 210 * 3);
        artifact.BorderVertices.ShouldNotBeEmpty();
        artifact.BorderIndices.ShouldNotBeEmpty();

        // Every Habitable & SeaZone face must rasterize into LocationIdGrid
        foreach (var face in atlas.Topology.Faces.Where(f => f.Kind is LocationKind.Habitable or LocationKind.SeaZone))
            artifact.LocationIdGrid.ShouldContain((ushort)face.FaceId);

        // Beyond-the-Bastion Veasperia wasteland continent must stay non-ownable in ProvinceIdGrid (0) with WastelandTerrainId
        var wasteX = (int)(0.980f * 360);
        var wasteY = (int)(0.500f * 210);
        var wasteIdx = wasteY * 360 + wasteX;
        artifact.ProvinceIdGrid[wasteIdx].ShouldBe((ushort)0);
        artifact.TerrainGrid[wasteIdx].ShouldBe(MapBakePipeline.WastelandTerrainId);
        artifact.LocationKindGrid[wasteIdx].ShouldBe((byte)LocationKind.Impassable);

        var tempFile = Path.Combine(Path.GetTempPath(), $"voracity_vmap_{Guid.NewGuid():N}.vmap");
        try
        {
            MapBakePipeline.SaveVmapCache(tempFile, artifact);
            File.Exists(tempFile).ShouldBeTrue();

            MapBakePipeline.TryLoadVmapCache(tempFile, "WRONG_HASH").ShouldBeNull();

            var loaded = MapBakePipeline.TryLoadVmapCache(tempFile, artifact.TopologyHash);
            loaded.ShouldNotBeNull();
            loaded!.ProvinceIdGrid.ShouldBe(artifact.ProvinceIdGrid);
            loaded.LocationIdGrid.ShouldBe(artifact.LocationIdGrid);
            loaded.LocationKindGrid.ShouldBe(artifact.LocationKindGrid);
            loaded.TerrainGrid.ShouldBe(artifact.TerrainGrid);
            loaded.ProvinceDataRgb.ShouldBe(artifact.ProvinceDataRgb);
            loaded.HeightCoastRgb.ShouldBe(artifact.HeightCoastRgb);
            loaded.BorderIndices.ShouldBe(artifact.BorderIndices);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void PlanarTopologyAndBake_1000Locations_ScalesWithinBudgetAndSupports16BitIds()
    {
        const int landCount = 850;
        const int seaCount = 150;
        var locations = new List<LocationDefinition>(landCount + seaCount);
        var rng = new Random(1380);

        for (var i = 1; i <= landCount; i++)
        {
            var ax = 0.08f + (float)rng.NextDouble() * 0.80f;
            var ay = 0.08f + (float)rng.NextDouble() * 0.84f;
            locations.Add(new LocationDefinition(
                Id: $"loc_land_{i}",
                NameKey: $"loc_{i}",
                DefaultName: $"Location {i}",
                Kind: LocationKind.Habitable,
                ProvinceId: $"loc_land_{i}",
                AreaId: $"area_{(i - 1) / 5}",
                RegionId: "region_1",
                SuperRegionId: "sr_1",
                ContinentId: "pontaria",
                OwnerNationId: i % 2 == 0 ? "AET" : "HAS",
                Rank: LocationRank.RuralSettlement,
                Topography: TopographyType.Flatland,
                Vegetation: VegetationType.Grasslands,
                Climate: ClimateType.Continental,
                RawGoodId: "grain",
                RgoMethod: RgoMethod.Farming,
                NaturalHarborSuitability: 10,
                Development: 5.0,
                RoadLevel: RoadLevel.None,
                InitialPopulation: 10000,
                SeedX: ax,
                SeedY: ay,
                BasePopulationCapacity: 40.0,
                IsCapital: i == 1,
                IsMarketCenter: false,
                MarketId: null,
                PersistentUid: i));
        }

        for (var s = 1; s <= seaCount; s++)
        {
            var fid = landCount + s;
            locations.Add(new LocationDefinition(
                Id: $"sea_zone_{s}",
                NameKey: $"sea_{s}",
                DefaultName: $"Sea {s}",
                Kind: LocationKind.SeaZone,
                ProvinceId: $"sea_zone_{s}",
                AreaId: string.Empty,
                RegionId: string.Empty,
                SuperRegionId: string.Empty,
                ContinentId: "pontaria",
                OwnerNationId: null,
                Rank: LocationRank.RuralSettlement,
                Topography: TopographyType.Ocean,
                Vegetation: VegetationType.Sea,
                Climate: ClimateType.Maritime,
                RawGoodId: null,
                RgoMethod: RgoMethod.Gathering,
                NaturalHarborSuitability: 0,
                Development: 0.0,
                RoadLevel: RoadLevel.None,
                InitialPopulation: 0,
                SeedX: (s % 15) / 15f + 0.03f,
                SeedY: (s / 15) < 5 ? 0.03f : 0.97f,
                BasePopulationCapacity: 0.0,
                IsCapital: false,
                IsMarketCenter: false,
                MarketId: null,
                SeaZoneMaritimePresence: 0.35,
                PersistentUid: fid));
        }

        var sw = Stopwatch.StartNew();
        var topo = PlanarTopologyBuilder.BuildFromLocations(locations, GeographyFeaturesData.Empty, new TopologyBuildOptions(720, 420));
        var artifact = MapBakePipeline.BakeFromTopology(topo, Array.Empty<ProvinceSnapshot>(), GeographyFeaturesData.Empty, width: 720, height: 420);
        sw.Stop();

        topo.Faces.Count.ShouldBe(landCount + seaCount);
        sw.ElapsedMilliseconds.ShouldBeLessThan(8000);

        var foundHighId = false;
        for (var p = 0; p < artifact.ProvinceIdGrid.Length; p++)
        {
            var id = artifact.ProvinceIdGrid[p];
            if (id > 512)
            {
                var encoded = artifact.ProvinceDataRgb[p * 3] | (artifact.ProvinceDataRgb[p * 3 + 1] << 8);
                encoded.ShouldBe(id);
                foundHighId = true;
                break;
            }
        }
        foundHighId.ShouldBeTrue("Expected >512 numerical location IDs encoded losslessly in 16-bit RGB channels");
    }

    [Fact]
    public void ComputeTopologyHash_ReactsToFeaturePointChanges()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var atlas = new EditableWorldAtlas(db);

        var h1 = MapBakePipeline.ComputeTopologyHash(atlas.Topology, db.GeographyFeatures, 1440, 840);
        var modifiedRivers = db.GeographyFeatures.Rivers.ToArray();
        var r0 = modifiedRivers[0];
        var pts = r0.ControlPoints.ToArray();
        pts[0] = new Point2D(pts[0].X + 0.025f, pts[0].Y);
        modifiedRivers[0] = r0 with { ControlPoints = pts };
        var modifiedFeatures = db.GeographyFeatures with { Rivers = modifiedRivers };

        var h2 = MapBakePipeline.ComputeTopologyHash(atlas.Topology, modifiedFeatures, 1440, 840);
        h1.ShouldNotBe(h2);
    }

    [Fact]
    public void TerrainNameToId_MapsAllBiomesDistinctly()
    {
        MapTerrainNoise.TerrainNameToId("plains").ShouldBe((byte)1);
        MapTerrainNoise.TerrainNameToId("farmlands").ShouldBe((byte)2);
        MapTerrainNoise.TerrainNameToId("forest").ShouldBe((byte)3);
        MapTerrainNoise.TerrainNameToId("hills").ShouldBe((byte)4);
        MapTerrainNoise.TerrainNameToId("mountains").ShouldBe((byte)5);
        MapTerrainNoise.TerrainNameToId("wetlands").ShouldBe((byte)6);
        MapTerrainNoise.TerrainNameToId("desert").ShouldBe((byte)7);
        MapTerrainNoise.TerrainNameToId("coastal").ShouldBe((byte)8);
    }

    [Fact]
    public void MapWorldGeometrySampler_ConvertsMetricCoordinatesAndSamplesElevationAndRivers()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var atlas = new EditableWorldAtlas(db);
        IMapWorldGeometry geom = new MapWorldGeometrySampler(atlas.Topology, db.GeographyFeatures);

        var (xKm, zKm) = geom.NormalizedToWorldKm(0.5f, 0.5f);
        xKm.ShouldBe(geom.WorldWidthKm * 0.5f, 0.01f);
        zKm.ShouldBe(geom.WorldHeightKm * 0.5f, 0.01f);

        var (nx, ny) = geom.WorldKmToNormalized(xKm, zKm);
        nx.ShouldBe(0.5f, 0.0001f);
        ny.ShouldBe(0.5f, 0.0001f);

        geom.IsWaterAt(0.01f, 0.01f).ShouldBeTrue();
        geom.SampleElevationMeters(0.01f, 0.01f).ShouldBe(0f);

        db.GeographyFeatures.MountainRanges.ShouldNotBeEmpty();
        var spinePt = db.GeographyFeatures.MountainRanges[0].SpinePoints[0];
        if (!geom.IsWaterAt(spinePt.X, spinePt.Y))
            geom.SampleElevationMeters(spinePt.X, spinePt.Y).ShouldBeGreaterThan(180f);

        geom.GetRiverSplinesKm().Count.ShouldBe(db.GeographyFeatures.Rivers.Length);
    }
}
