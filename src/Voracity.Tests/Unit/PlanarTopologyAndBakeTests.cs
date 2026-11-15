using System.Diagnostics;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Mapping;
using Voracity.Core.Models;
using Voracity.Tests.Integration;

namespace Voracity.Tests.Unit;

public sealed class PlanarTopologyAndBakeTests
{
    [Fact]
    public void PlanarTopologyBuilder_EnforcesSymmetricAdjacenciesAndWatertightArcs()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var atlas = new EditableWorldAtlas(db);
        var topo = atlas.Topology;

        Assert.Equal(134, topo.Faces.Count);
        Assert.NotEmpty(topo.Vertices);
        Assert.NotEmpty(topo.Arcs);

        // Every planar adjacency pair must be symmetric and map to real faces
        var adjPairs = new HashSet<(int A, int B)>();
        foreach (var (a, b) in topo.SymmetricProvinceAdjacencies)
        {
            Assert.NotEqual(a, b);
            Assert.True(topo.FaceById.ContainsKey(a));
            Assert.True(topo.FaceById.ContainsKey(b));
            adjPairs.Add((a, b));
        }

        // Every shared land border arc (LeftFaceId > 0 && RightFaceId > 0) must be represented symmetrically
        foreach (var arc in topo.Arcs)
        {
            Assert.True(arc.Points.Length >= 2);
            if (arc.LeftFaceId > 0 && arc.RightFaceId > 0)
            {
                var pair = arc.LeftFaceId < arc.RightFaceId
                    ? (arc.LeftFaceId, arc.RightFaceId)
                    : (arc.RightFaceId, arc.LeftFaceId);
                Assert.Contains(pair, adjPairs);
            }
            if (arc.RightFaceId == 0 && arc.LeftFaceId > 0)
            {
                Assert.True(arc.IsCoastline);
                Assert.True(topo.FaceById[arc.LeftFaceId].IsCoastal);
            }
        }

        // Deep inland province should not border ocean (RightFaceId == 0)
        var inlandFace = topo.FaceByProvinceId["prov_briggston"];
        Assert.False(inlandFace.IsCoastal);
    }

    [Fact]
    public void MapBakePipeline_BakeAndVmapCacheRoundtrip_PreservesAllGridsAndHashes()
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

        Assert.Equal(360 * 210, artifact.ProvinceIdGrid.Length);
        Assert.Equal(360 * 210 * 3, artifact.HeightCoastRgb.Length);
        Assert.NotEmpty(artifact.BorderVertices);
        Assert.NotEmpty(artifact.BorderIndices);

        // Every face must rasterize at least 1 pixel
        foreach (var face in atlas.Topology.Faces)
            Assert.Contains((ushort)face.FaceId, artifact.ProvinceIdGrid);

        // Beyond-the-Bastion Veasperia wasteland continent must remain unclickable (ProvinceId=0) with TerrainId=WastelandTerrainId
        var wasteX = (int)(0.980f * 360);
        var wasteY = (int)(0.500f * 210);
        var wasteIdx = wasteY * 360 + wasteX;
        Assert.Equal((ushort)0, artifact.ProvinceIdGrid[wasteIdx]);
        Assert.Equal(MapBakePipeline.WastelandTerrainId, artifact.TerrainGrid[wasteIdx]);

        var tempFile = Path.Combine(Path.GetTempPath(), $"voracity_vmap_{Guid.NewGuid():N}.vmap");
        try
        {
            MapBakePipeline.SaveVmapCache(tempFile, artifact);
            Assert.True(File.Exists(tempFile));

            // Mismatched hash must cleanly miss cache
            Assert.Null(MapBakePipeline.TryLoadVmapCache(tempFile, "WRONG_HASH"));

            // Matching hash must load pixel-for-pixel identical baked grids & border ribbons
            var loaded = MapBakePipeline.TryLoadVmapCache(tempFile, artifact.TopologyHash);
            Assert.NotNull(loaded);
            Assert.Equal(artifact.ProvinceIdGrid, loaded!.ProvinceIdGrid);
            Assert.Equal(artifact.TerrainGrid, loaded.TerrainGrid);
            Assert.Equal(artifact.ProvinceDataRgb, loaded.ProvinceDataRgb);
            Assert.Equal(artifact.HeightCoastRgb, loaded.HeightCoastRgb);
            Assert.Equal(artifact.BorderVertices.Length, loaded.BorderVertices.Length);
            Assert.Equal(artifact.BorderIndices, loaded.BorderIndices);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void FastSpatialVoronoi_2500Provinces_Supports65kIndexingUnderBudget()
    {
        const int provCount = 2500;
        var provs = new List<ProvinceSnapshot>(provCount);
        var rng = new Random(1380);
        for (var i = 1; i <= provCount; i++)
        {
            var ax = 0.06f + (float)rng.NextDouble() * 0.88f;
            var ay = 0.08f + (float)rng.NextDouble() * 0.84f;
            provs.Add(new ProvinceSnapshot(
                NumericalIndex: i,
                ProvinceId: $"prov_bench_{i}",
                Name: $"Prov {i}",
                StateId: $"state_bench_{(i - 1) / 4}",
                OwnerNationId: i % 2 == 0 ? "AET" : "HAS",
                Terrain: "plains",
                HubType: "city",
                IsCapital: false,
                IsCoastal: false,
                AnchorX: ax,
                AnchorY: ay,
                HoldingLevel: 1,
                FortLevel: 0,
                ResourcePotential: "grain",
                OccupiedByNationId: null));
        }

        var sw = Stopwatch.StartNew();
        var raster = PontariaMapGenerator.Generate(provs, width: 720, height: 420);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2500, $"Expected fast spatial bake < 2500ms, took {sw.ElapsedMilliseconds}ms");

        // Verify 16-bit province IDs > 511 (beyond legacy LookupWidth=512 limit) roundtrip cleanly in ProvinceDataRgb
        var foundHighId = false;
        for (var p = 0; p < raster.ProvinceIndexGrid.Length; p++)
        {
            var id = raster.ProvinceIndexGrid[p];
            if (id > 512)
            {
                var encoded = raster.ProvinceDataRgb[p * 3] | (raster.ProvinceDataRgb[p * 3 + 1] << 8);
                Assert.Equal(id, encoded);
                foundHighId = true;
                break;
            }
        }
        Assert.True(foundHighId, "Expected >512 numerical province IDs encoded losslessly in 16-bit RGB channels");
    }

    [Fact]
    public void MapWorldGeometrySampler_ConvertsMetricCoordinatesAndSamplesElevationAndRivers()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var atlas = new EditableWorldAtlas(db);
        IMapWorldGeometry geom = new MapWorldGeometrySampler(atlas.Topology, db.GeographyFeatures);

        var (xKm, zKm) = geom.NormalizedToWorldKm(0.5f, 0.5f);
        Assert.Equal(geom.WorldWidthKm * 0.5f, xKm, precision: 2);
        Assert.Equal(geom.WorldHeightKm * 0.5f, zKm, precision: 2);

        var (nx, ny) = geom.WorldKmToNormalized(xKm, zKm);
        Assert.Equal(0.5f, nx, precision: 4);
        Assert.Equal(0.5f, ny, precision: 4);

        // Extreme corner ocean must report IsWaterAt = true and 0m elevation
        Assert.True(geom.IsWaterAt(0.01f, 0.01f));
        Assert.Equal(0f, geom.SampleElevationMeters(0.01f, 0.01f));

        // Mountain spine center must report elevation above base terrain
        Assert.NotEmpty(db.GeographyFeatures.MountainRanges);
        var spinePt = db.GeographyFeatures.MountainRanges[0].SpinePoints[0];
        if (!geom.IsWaterAt(spinePt.X, spinePt.Y))
            Assert.True(geom.SampleElevationMeters(spinePt.X, spinePt.Y) > 180f);

        var riverSplines = geom.GetRiverSplinesKm();
        Assert.Equal(db.GeographyFeatures.Rivers.Length, riverSplines.Count);
    }
}
