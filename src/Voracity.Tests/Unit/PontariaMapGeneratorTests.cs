using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Tests.Integration;

namespace Voracity.Tests.Unit;

public sealed class PontariaMapGeneratorTests
{
    [Fact]
    public void Generate_ProducesContiguousLandCoverageForEveryProvinceAndRealAdjacencies()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        var world = new SimulationWorld(db, "AET", 1380UL);
        var snap = world.BuildRenderSnapshot();

        var rasterA = PontariaMapGenerator.Generate(snap.Provinces, width: 480, height: 280);
        var rasterB = PontariaMapGenerator.Generate(snap.Provinces, width: 480, height: 280);

        Assert.Equal(rasterA.ProvinceIndexGrid, rasterB.ProvinceIndexGrid);
        Assert.Equal(rasterA.ProvinceDataRgb, rasterB.ProvinceDataRgb);
        Assert.Equal(rasterA.HeightCoastRgb, rasterB.HeightCoastRgb);

        // Sea corners stay ocean (0)
        Assert.Equal(0, rasterA.LookupProvinceIndex(0.02, 0.02));
        Assert.Equal(0, rasterA.LookupProvinceIndex(0.98, 0.98));

        // Every defined province must exist with real pixels and lookup must match its NumericalIndex at centroid
        foreach (var prov in snap.Provinces)
        {
            Assert.True(rasterA.ProvinceCentroids.TryGetValue(prov.NumericalIndex, out var centroid),
                $"Province {prov.ProvinceId} ({prov.NumericalIndex}) is missing raster pixels.");

            var lookedUp = rasterA.LookupProvinceIndex(centroid.X, centroid.Y);
            Assert.Equal(prov.NumericalIndex, lookedUp);
        }

        Assert.NotEmpty(rasterA.ProvinceAdjacencies);
        Assert.Equal(snap.States.Count, rasterA.StateCentroids.Count);
        Assert.Equal(db.Nations.Count, rasterA.NationLabelAnchors.Count);
    }

    [Fact]
    public void TerrainNameToId_MapsAllBiomesDistinctly()
    {
        Assert.Equal((byte)1, PontariaMapGenerator.TerrainNameToId("plains"));
        Assert.Equal((byte)2, PontariaMapGenerator.TerrainNameToId("farmlands"));
        Assert.Equal((byte)3, PontariaMapGenerator.TerrainNameToId("forest"));
        Assert.Equal((byte)4, PontariaMapGenerator.TerrainNameToId("hills"));
        Assert.Equal((byte)5, PontariaMapGenerator.TerrainNameToId("mountains"));
        Assert.Equal((byte)6, PontariaMapGenerator.TerrainNameToId("wetlands"));
        Assert.Equal((byte)7, PontariaMapGenerator.TerrainNameToId("desert"));
        Assert.Equal((byte)8, PontariaMapGenerator.TerrainNameToId("coastal"));
    }
}
