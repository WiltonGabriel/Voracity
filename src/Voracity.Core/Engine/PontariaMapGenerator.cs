using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Core.Engine;

public sealed record PontariaMapRaster(
    int Width,
    int Height,
    ushort[] ProvinceIndexGrid,
    byte[] TerrainGrid,
    byte[] ProvinceDataRgb,
    byte[] HeightCoastRgb,
    IReadOnlyDictionary<int, (float X, float Y)> ProvinceCentroids,
    IReadOnlyDictionary<string, (float X, float Y)> StateCentroids,
    IReadOnlyDictionary<string, (float X, float Y, float SpanWidth)> NationLabelAnchors,
    IReadOnlyList<(int ProvA, int ProvB)> ProvinceAdjacencies)
{
    public int LookupProvinceIndex(double normX, double normY)
    {
        if (normX < 0.0 || normX >= 1.0 || normY < 0.0 || normY >= 1.0)
            return 0;

        var px = Math.Clamp((int)(normX * Width), 0, Width - 1);
        var py = Math.Clamp((int)(normY * Height), 0, Height - 1);
        return ProvinceIndexGrid[py * Width + px];
    }
}

/// <summary>
/// Deterministic cartographic rasterizer inspired by OpenVic / CK3 / Victoria 3.
/// Builds contiguous domain-warped province polygons, coastlines, heightmap relief, O(1) lookup, and real border adjacencies.
/// </summary>
public static class PontariaMapGenerator
{
    public const int DefaultWidth = 1440;
    public const int DefaultHeight = 840;

    public static byte TerrainNameToId(string terrain) => terrain switch
    {
        "plains" => 1,
        "farmlands" => 2,
        "forest" => 3,
        "hills" => 4,
        "mountains" => 5,
        "wetlands" => 6,
        "desert" => 7,
        "coastal" => 8,
        _ => 1
    };

    public static BakedMapArtifact GenerateBaked(
        IReadOnlyList<ProvinceSnapshot> provinces,
        GeographyFeaturesData? features = null,
        PlanarMapTopology? topology = null,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        var topo = topology ?? PlanarTopologyBuilder.BuildFromProvinces(provinces, features);
        return MapBakePipeline.BakeFromTopology(topo, provinces, features, width, height);
    }

    public static PontariaMapRaster Generate(
        IReadOnlyList<ProvinceSnapshot> provinces,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        // High-speed uniform spatial grid path for dense 2,500+ province procedural stress bakes
        if (provinces.Count > 350)
            return GenerateFastSpatialVoronoi(provinces, width, height);

        var count = width * height;
        var provinceGrid = new ushort[count];
        var terrainGrid = new byte[count];
        var provinceDataRgb = new byte[count * 3];
        var heightCoastRgb = new byte[count * 3];

        var pCount = provinces.Count;
        var pxX = new float[pCount];
        var pxY = new float[pCount];
        var pTerrainId = new byte[pCount];
        var pCoastal = new bool[pCount];

        for (var i = 0; i < pCount; i++)
        {
            pxX[i] = provinces[i].AnchorX;
            pxY[i] = provinces[i].AnchorY;
            pTerrainId[i] = TerrainNameToId(provinces[i].Terrain);
            pCoastal[i] = provinces[i].IsCoastal;
        }

        // Aspect ratio factor (~1.71:1) so Voronoi cells are visually well-proportioned
        const float aspectX = 1.714f;
        var maxIdx = Math.Max(pCount, provinces.Max(p => p.NumericalIndex));
        var sumX = new double[maxIdx + 1];
        var sumY = new double[maxIdx + 1];
        var counts = new int[maxIdx + 1];

        var elevationFloat = new float[count];
        var maxDistSqField = new float[count];

        // 1. Domain-warped Voronoi + smooth continental envelope
        for (var y = 0; y < height; y++)
        {
            var ny = (y + 0.5f) / height;
            var rowOffset = y * width;

            for (var x = 0; x < width; x++)
            {
                var nx = (x + 0.5f) / width;
                var idx = rowOffset + x;

                // Two-octave organic domain warp for natural borders & jagged coastlines
                var wx = nx + Fbm2D(nx * 11.5f + 3.1f, ny * 8.2f + 1.7f) * 0.026f;
                var wy = ny + Fbm2D(nx * 10.8f + 9.4f, ny * 9.1f + 5.3f) * 0.024f;

                // Evaluate continental landmass envelope from soft union of province anchor influence
                var bestDistSq = float.MaxValue;
                var secondDistSq = float.MaxValue;
                var bestIdx = -1;
                var landPotential = 0.0f;

                for (var p = 0; p < pCount; p++)
                {
                    var dx = (wx - pxX[p]) * aspectX;
                    var dy = wy - pxY[p];
                    var dSq = dx * dx + dy * dy;

                    if (dSq < bestDistSq)
                    {
                        secondDistSq = bestDistSq;
                        bestDistSq = dSq;
                        bestIdx = p;
                    }
                    else if (dSq < secondDistSq)
                    {
                        secondDistSq = dSq;
                    }

                    // Soft metaball contribution bridges adjacent states cleanly into 1 continuous continent
                    var radiusSq = pCoastal[p] ? 0.0145f : 0.0175f;
                    var contrib = radiusSq / (dSq + 0.0022f);
                    if (contrib > landPotential)
                        landPotential = contrib;
                    else
                        landPotential += contrib * 0.28f;
                }

                // Carve natural bays (Levant SW Gulf, Zarima South Bay) & screen edges
                var coastNoise = Fbm2D(nx * 19.0f + 14.2f, ny * 14.5f + 8.7f) * 0.34f;
                var gulfs = 0.0f;
                if (nx > 0.21f && nx < 0.39f && ny > 0.68f) gulfs += 0.45f; // Levant/Varakrima Western Bay
                if (nx > 0.47f && nx < 0.66f && ny > 0.85f) gulfs += 0.40f; // Sea of Zarima
                if (ny < 0.065f || ny > 0.91f || nx < 0.045f || nx > 0.94f) gulfs += 0.85f;

                var effectivePotential = landPotential + coastNoise - gulfs;
                var isLand = bestIdx >= 0 && effectivePotential >= 1.18f && bestDistSq < 0.032f;

                maxDistSqField[idx] = bestDistSq;

                if (isLand)
                {
                    var provNum = (ushort)provinces[bestIdx].NumericalIndex;
                    provinceGrid[idx] = provNum;
                    var tId = pTerrainId[bestIdx];
                    terrainGrid[idx] = tId;

                    provinceDataRgb[idx * 3 + 0] = (byte)(provNum & 0xFF);
                    provinceDataRgb[idx * 3 + 1] = (byte)((provNum >> 8) & 0xFF);
                    provinceDataRgb[idx * 3 + 2] = tId;

                    sumX[provNum] += nx;
                    sumY[provNum] += ny;
                    counts[provNum]++;

                    elevationFloat[idx] = ComputeBaseElevation(tId, nx, ny, effectivePotential);
                }
                else
                {
                    provinceGrid[idx] = 0;
                    terrainGrid[idx] = 0;
                    // Encode shallow shelf signal in elevationFloat (0..0.48 range for water)
                    var shelf = Math.Clamp(effectivePotential / 1.18f, 0f, 1f);
                    elevationFloat[idx] = shelf * 0.45f;
                }
            }
        }

        // 2. Compute smooth distance-to-coast & ridge shading for HeightCoastRgb
        for (var i = 0; i < count; i++)
        {
            var y = i / width;
            var x = i - y * width;
            var nx = (x + 0.5f) / width;
            var ny = (y + 0.5f) / height;

            var elev = elevationFloat[i];
            var coastProx = ComputeLocalCoastProximity(provinceGrid, width, height, x, y);
            var parchmentGrain = 0.5f + 0.45f * Fbm2D(nx * 46.0f + 2.3f, ny * 34.0f + 7.1f);

            heightCoastRgb[i * 3 + 0] = (byte)Math.Clamp((int)(elev * 255f), 0, 255);
            heightCoastRgb[i * 3 + 1] = (byte)Math.Clamp((int)(coastProx * 255f), 0, 255);
            heightCoastRgb[i * 3 + 2] = (byte)Math.Clamp((int)(parchmentGrain * 255f), 0, 255);
        }

        // 3. Centroids & real pixel border adjacencies
        var provCentroids = new Dictionary<int, (float X, float Y)>(pCount);
        var stateAccum = new Dictionary<string, (double X, double Y, int Count)>(StringComparer.Ordinal);
        var nationAccum = new Dictionary<string, (double X, double Y, float MinX, float MaxX, int Count)>(StringComparer.Ordinal);

        foreach (var p in provinces)
        {
            var c = counts[p.NumericalIndex];
            var cx = c > 0 ? (float)(sumX[p.NumericalIndex] / c) : p.AnchorX;
            var cy = c > 0 ? (float)(sumY[p.NumericalIndex] / c) : p.AnchorY;
            provCentroids[p.NumericalIndex] = (cx, cy);

            var st = stateAccum.GetValueOrDefault(p.StateId);
            stateAccum[p.StateId] = (st.X + cx * Math.Max(1, c), st.Y + cy * Math.Max(1, c), st.Count + Math.Max(1, c));

            var nt = nationAccum.TryGetValue(p.OwnerNationId, out var curN)
                ? curN
                : (0.0, 0.0, 1.0f, 0.0f, 0);
            nationAccum[p.OwnerNationId] = (
                nt.Item1 + cx * Math.Max(1, c),
                nt.Item2 + cy * Math.Max(1, c),
                Math.Min(nt.Item3, cx),
                Math.Max(nt.Item4, cx),
                nt.Item5 + Math.Max(1, c));
        }

        var stateCentroids = stateAccum.ToDictionary(
            k => k.Key,
            v => ((float)(v.Value.X / v.Value.Count), (float)(v.Value.Y / v.Value.Count)),
            StringComparer.Ordinal);

        var nationAnchors = nationAccum.ToDictionary(
            k => k.Key,
            v => ((float)(v.Value.X / v.Value.Count), (float)(v.Value.Y / v.Value.Count), Math.Max(0.06f, v.Value.MaxX - v.Value.MinX)),
            StringComparer.Ordinal);

        var edgeSet = new HashSet<long>();
        var edges = new List<(int ProvA, int ProvB)>(192);
        for (var y = 0; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 0; x < width - 1; x++)
            {
                var a = provinceGrid[row + x];
                if (a == 0)
                    continue;

                var right = provinceGrid[row + x + 1];
                if (right != 0 && right != a)
                    AddEdge(a, right, edgeSet, edges);

                var down = provinceGrid[row + width + x];
                if (down != 0 && down != a)
                    AddEdge(a, down, edgeSet, edges);
            }
        }

        return new PontariaMapRaster(
            width,
            height,
            provinceGrid,
            terrainGrid,
            provinceDataRgb,
            heightCoastRgb,
            provCentroids,
            stateCentroids,
            nationAnchors,
            edges);
    }

    private static void AddEdge(int a, int b, HashSet<long> set, List<(int, int)> list)
    {
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        var key = ((long)lo << 20) | (uint)hi;
        if (set.Add(key))
            list.Add((lo, hi));
    }

    private static float ComputeLocalCoastProximity(ushort[] grid, int width, int height, int x, int y)
    {
        var selfIsLand = grid[y * width + x] != 0;
        var minDist = 9;
        const int radius = 6;

        for (var dy = -radius; dy <= radius; dy += 2)
        {
            var ny = y + dy;
            if (ny < 0 || ny >= height) continue;
            for (var dx = -radius; dx <= radius; dx += 2)
            {
                var nx = x + dx;
                if (nx < 0 || nx >= width) continue;
                var otherIsLand = grid[ny * width + nx] != 0;
                if (otherIsLand != selfIsLand)
                {
                    var d = Math.Abs(dx) + Math.Abs(dy);
                    if (d < minDist)
                        minDist = d;
                }
            }
        }

        return Math.Clamp(1.0f - (minDist - 1) / 8.0f, 0.0f, 1.0f);
    }

    private static float ComputeBaseElevation(byte terrainId, float nx, float ny, float potential)
    {
        var baseH = terrainId switch
        {
            5 => 0.84f, // mountains
            4 => 0.72f, // hills
            3 => 0.62f, // forest
            7 => 0.59f, // desert
            1 => 0.56f, // plains
            2 => 0.54f, // farmlands
            6 => 0.51f, // wetlands
            8 => 0.50f, // coastal
            _ => 0.55f
        };

        var ridge = Math.Abs(Fbm2D(nx * 24.0f + 4.1f, ny * 18.0f + 2.9f)) * (terrainId == 5 ? 0.16f : 0.08f);
        var coastalSlope = Math.Clamp((potential - 1.18f) * 0.06f, 0f, 0.06f);
        return Math.Clamp(baseH + ridge + coastalSlope, 0.48f, 1.0f);
    }

    private static PontariaMapRaster GenerateFastSpatialVoronoi(
        IReadOnlyList<ProvinceSnapshot> provinces,
        int width,
        int height)
    {
        var count = width * height;
        var provinceGrid = new ushort[count];
        var terrainGrid = new byte[count];
        var provinceDataRgb = new byte[count * 3];
        var heightCoastRgb = new byte[count * 3];

        var spatial = new SpatialGrid2D<int>(64, 36);
        var maxNum = 1;
        for (var i = 0; i < provinces.Count; i++)
        {
            spatial.Add(provinces[i].AnchorX, provinces[i].AnchorY, i);
            if (provinces[i].NumericalIndex > maxNum)
                maxNum = provinces[i].NumericalIndex;
        }

        var sumX = new double[maxNum + 1];
        var sumY = new double[maxNum + 1];
        var counts = new int[maxNum + 1];
        const float aspectX = 1.714f;

        for (var y = 0; y < height; y++)
        {
            var ny = (y + 0.5f) / height;
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var nx = (x + 0.5f) / width;
                var idx = row + x;
                if (nx < 0.04f || nx > 0.96f || ny < 0.05f || ny > 0.95f)
                    continue;

                var slot = spatial.FindNearest(nx, ny, aspectX, 0.16f);
                if (slot < 0)
                    continue;
                var p = provinces[spatial.GetAt(slot).Item];
                var pNum = (ushort)p.NumericalIndex;
                var tId = TerrainNameToId(p.Terrain);
                provinceGrid[idx] = pNum;
                terrainGrid[idx] = tId;
                provinceDataRgb[idx * 3 + 0] = (byte)(pNum & 0xFF);
                provinceDataRgb[idx * 3 + 1] = (byte)((pNum >> 8) & 0xFF);
                provinceDataRgb[idx * 3 + 2] = tId;

                sumX[pNum] += nx;
                sumY[pNum] += ny;
                counts[pNum]++;
            }
        }

        var provCentroids = new Dictionary<int, (float X, float Y)>(provinces.Count);
        var stateAccum = new Dictionary<string, (double X, double Y, int Count)>(StringComparer.Ordinal);
        var nationAccum = new Dictionary<string, (double X, double Y, float MinX, float MaxX, int Count)>(StringComparer.Ordinal);
        foreach (var p in provinces)
        {
            var c = counts[p.NumericalIndex];
            var cx = c > 0 ? (float)(sumX[p.NumericalIndex] / c) : p.AnchorX;
            var cy = c > 0 ? (float)(sumY[p.NumericalIndex] / c) : p.AnchorY;
            provCentroids[p.NumericalIndex] = (cx, cy);
            var st = stateAccum.GetValueOrDefault(p.StateId);
            stateAccum[p.StateId] = (st.X + cx, st.Y + cy, st.Count + 1);
            var nt = nationAccum.TryGetValue(p.OwnerNationId, out var curN) ? curN : (0.0, 0.0, 1.0f, 0.0f, 0);
            nationAccum[p.OwnerNationId] = (nt.Item1 + cx, nt.Item2 + cy, Math.Min(nt.Item3, cx), Math.Max(nt.Item4, cx), nt.Item5 + 1);
        }

        var stateCentroids = stateAccum.ToDictionary(k => k.Key, v => ((float)(v.Value.X / v.Value.Count), (float)(v.Value.Y / v.Value.Count)), StringComparer.Ordinal);
        var nationAnchors = nationAccum.ToDictionary(k => k.Key, v => ((float)(v.Value.X / v.Value.Count), (float)(v.Value.Y / v.Value.Count), Math.Max(0.06f, v.Value.MaxX - v.Value.MinX)), StringComparer.Ordinal);

        return new PontariaMapRaster(width, height, provinceGrid, terrainGrid, provinceDataRgb, heightCoastRgb, provCentroids, stateCentroids, nationAnchors, Array.Empty<(int, int)>());
    }

    internal static float Fbm2D(float x, float y)
    {
        return 0.60f * ValueNoise2D(x, y)
             + 0.28f * ValueNoise2D(x * 2.07f + 11.3f, y * 2.03f + 7.7f)
             + 0.12f * ValueNoise2D(x * 4.19f + 23.1f, y * 4.11f + 17.9f);
    }

    private static float ValueNoise2D(float x, float y)
    {
        var ix = (int)MathF.Floor(x);
        var iy = (int)MathF.Floor(y);
        var fx = x - ix;
        var fy = y - iy;

        var u = fx * fx * (3f - 2f * fx);
        var v = fy * fy * (3f - 2f * fy);

        var a = Hash01(ix, iy);
        var b = Hash01(ix + 1, iy);
        var c = Hash01(ix, iy + 1);
        var d = Hash01(ix + 1, iy + 1);

        var lerp1 = a + (b - a) * u;
        var lerp2 = c + (d - c) * u;
        return (lerp1 + (lerp2 - lerp1) * v) * 2f - 1f;
    }

    private static float Hash01(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0x00FFFFFFu) / 16777215.0f;
        }
    }
}
