using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Core.Mapping;

/// <summary>
/// GPU-ready border ribbon vertex exported per shared arc segment for 1-draw-call ArrayMesh rendering.
/// LeftFaceId and RightFaceId allow the shader to read dynamic political/lens state from ProvinceGpuLookup
/// without rebuilding geometry when provinces change ownership.
/// </summary>
public readonly record struct BorderRibbonVertex(
    float X,
    float Y,
    float OffsetX,
    float OffsetY,
    int LeftFaceId,
    int RightFaceId,
    byte BorderFlags);

public sealed record BakedMapArtifact(
    int Width,
    int Height,
    ushort[] ProvinceIdGrid,
    byte[] TerrainGrid,
    byte[] ProvinceDataRgb,
    byte[] HeightCoastRgb,
    BorderRibbonVertex[] BorderVertices,
    int[] BorderIndices,
    IReadOnlyDictionary<int, (float X, float Y)> ProvinceCentroids,
    IReadOnlyDictionary<string, (float X, float Y)> StateCentroids,
    IReadOnlyDictionary<string, (float X, float Y, float SpanWidth)> NationLabelAnchors,
    string TopologyHash)
{
    public PontariaMapRaster ToLegacyRaster(IReadOnlyList<(int ProvA, int ProvB)> adjacencies)
        => new(
            Width,
            Height,
            ProvinceIdGrid,
            TerrainGrid,
            ProvinceDataRgb,
            HeightCoastRgb,
            ProvinceCentroids,
            StateCentroids,
            NationLabelAnchors,
            adjacencies);
}

/// <summary>
/// High-performance derived raster + batched border mesh baker.
/// Takes authoritative PlanarMapTopology (or rapid SpatialGrid Voronoi for dense benchmarks) and generates:
/// 1) 16-bit Province ID + Terrain RGB8 texture (up to 65,535 provinces)
/// 2) O(W*H) Two-Pass Chamfer Distance-to-Coast + Elevation + Mountain Ridge + Riverbed + Parchment Grain texture
/// 3) Batched Shared-Arc Border ArrayMesh data for 1-draw-call rendering
/// 4) Deterministic .vmap binary Deflate cache keyed by TopologyHash
/// </summary>
public static class MapBakePipeline
{
    public const int DefaultWidth = 1440;
    public const int DefaultHeight = 840;
    public const byte WastelandTerrainId = 250;

    public static BakedMapArtifact BakeFromTopology(
        PlanarMapTopology topology,
        IReadOnlyList<ProvinceSnapshot> provinces,
        GeographyFeaturesData? features = null,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        features ??= GeographyFeaturesData.Empty;
        var count = width * height;
        var provinceGrid = new ushort[count];
        var terrainGrid = new byte[count];
        var provinceDataRgb = new byte[count * 3];
        var heightCoastRgb = new byte[count * 3];

        var provById = new Dictionary<int, ProvinceSnapshot>(provinces.Count);
        var terrainByFace = new Dictionary<int, byte>(provinces.Count);
        foreach (var p in provinces)
        {
            provById[p.NumericalIndex] = p;
            terrainByFace[p.NumericalIndex] = PontariaMapGenerator.TerrainNameToId(p.Terrain);
        }

        // 1. Scanline polygon fill of every vector FaceRecord onto the derived ID grid (watertight, O(Faces * edges))
        var intersections = new List<float>(32);
        var minY = height;
        var maxY = 0;

        foreach (var face in topology.Faces)
        {
            var ring = face.OuterRing;
            if (ring.Length < 3)
                continue;

            var faceMinY = float.MaxValue;
            var faceMaxY = float.MinValue;
            for (var i = 0; i < ring.Length; i++)
            {
                if (ring[i].Y < faceMinY) faceMinY = ring[i].Y;
                if (ring[i].Y > faceMaxY) faceMaxY = ring[i].Y;
            }

            var yStart = Math.Clamp((int)MathF.Floor(faceMinY * height), 0, height - 1);
            var yEnd = Math.Clamp((int)MathF.Ceiling(faceMaxY * height), 0, height - 1);
            if (yStart < minY) minY = yStart;
            if (yEnd > maxY) maxY = yEnd;

            var faceUshort = (ushort)Math.Clamp(face.FaceId, 1, ushort.MaxValue);
            var tId = terrainByFace.GetValueOrDefault(face.FaceId, PontariaMapGenerator.TerrainNameToId(face.Terrain));

            for (var py = yStart; py <= yEnd; py++)
            {
                var ny = (py + 0.5f) / height;
                intersections.Clear();

                var j = ring.Length - 1;
                for (var i = 0; i < ring.Length; i++)
                {
                    var yi = ring[i].Y;
                    var yj = ring[j].Y;
                    if ((yi <= ny && yj > ny) || (yj <= ny && yi > ny))
                    {
                        var x = ring[i].X + (ny - yi) / (yj - yi) * (ring[j].X - ring[i].X);
                        intersections.Add(x);
                    }
                    j = i;
                }

                if (intersections.Count < 2)
                    continue;
                intersections.Sort();

                var rowBase = py * width;
                for (var k = 0; k + 1 < intersections.Count; k += 2)
                {
                    var x0 = Math.Clamp((int)MathF.Ceiling(intersections[k] * width - 0.5f), 0, width - 1);
                    var x1 = Math.Clamp((int)MathF.Floor(intersections[k + 1] * width - 0.5f), 0, width - 1);
                    for (var px = x0; px <= x1; px++)
                    {
                        var idx = rowBase + px;
                        provinceGrid[idx] = faceUshort;
                        terrainGrid[idx] = tId;
                    }
                }
            }
        }

        // Fill any microscopic 1-px scanline seam inside land with nearest neighbor face so interior is 100% solid
        var tmpCopy = (ushort[])provinceGrid.Clone();
        for (var y = 1; y < height - 1; y++)
        {
            var rowBase = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var idx = rowBase + x;
                if (tmpCopy[idx] != 0)
                    continue;
                var l = tmpCopy[idx - 1];
                var r = tmpCopy[idx + 1];
                var u = tmpCopy[idx - width];
                var d = tmpCopy[idx + width];
                if (l > 0 && r > 0 && u > 0 && d > 0)
                {
                    provinceGrid[idx] = l;
                    terrainGrid[idx] = terrainByFace.GetValueOrDefault(l, (byte)1);
                }
            }
        }

        // 1b. Rasterize inaccessible wasteland_continent polygons (provNum stays 0 so non-interactive; terrainGrid=250 for Fog of War)
        foreach (var lm in features.Landmasses)
        {
            if (!string.Equals(lm.Kind, "wasteland_continent", StringComparison.Ordinal) || lm.PolygonPoints.Length < 3)
                continue;

            var ring = lm.PolygonPoints;
            var wMinY = float.MaxValue;
            var wMaxY = float.MinValue;
            for (var i = 0; i < ring.Length; i++)
            {
                if (ring[i].Y < wMinY) wMinY = ring[i].Y;
                if (ring[i].Y > wMaxY) wMaxY = ring[i].Y;
            }

            var wyStart = Math.Clamp((int)MathF.Floor(wMinY * height), 0, height - 1);
            var wyEnd = Math.Clamp((int)MathF.Ceiling(wMaxY * height), 0, height - 1);
            for (var py = wyStart; py <= wyEnd; py++)
            {
                var ny = (py + 0.5f) / height;
                intersections.Clear();

                var j = ring.Length - 1;
                for (var i = 0; i < ring.Length; i++)
                {
                    var yi = ring[i].Y;
                    var yj = ring[j].Y;
                    if ((yi <= ny && yj > ny) || (yj <= ny && yi > ny))
                    {
                        var x = ring[i].X + (ny - yi) / (yj - yi) * (ring[j].X - ring[i].X);
                        intersections.Add(x);
                    }
                    j = i;
                }

                if (intersections.Count < 2)
                    continue;
                intersections.Sort();

                var rowBase = py * width;
                for (var k = 0; k + 1 < intersections.Count; k += 2)
                {
                    var x0 = Math.Clamp((int)MathF.Ceiling(intersections[k] * width - 0.5f), 0, width - 1);
                    var x1 = Math.Clamp((int)MathF.Floor(intersections[k + 1] * width - 0.5f), 0, width - 1);
                    for (var px = x0; px <= x1; px++)
                    {
                        var idx = rowBase + px;
                        if (provinceGrid[idx] == 0)
                            terrainGrid[idx] = WastelandTerrainId;
                    }
                }
            }
        }

        // 2. O(W * H) Dual-Pass Chamfer Distance-to-Coast UDF (replaces old O(W*H*121) nested scan!)
        var distToCoastPx = ComputeDualPassChamferCoastDistance(provinceGrid, terrainGrid, width, height);

        // 3. Pack ProvinceDataRgb, HeightCoastRgb & Centroids
        var sumX = new double[Math.Max(2, provinces.Max(p => p.NumericalIndex) + 2)];
        var sumY = new double[sumX.Length];
        var counts = new int[sumX.Length];

        for (var i = 0; i < count; i++)
        {
            var provNum = provinceGrid[i];
            var tId = terrainGrid[i];

            provinceDataRgb[i * 3 + 0] = (byte)(provNum & 0xFF);
            provinceDataRgb[i * 3 + 1] = (byte)((provNum >> 8) & 0xFF);
            provinceDataRgb[i * 3 + 2] = tId;

            var y = i / width;
            var x = i - y * width;
            var nx = (x + 0.5f) / width;
            var ny = (y + 0.5f) / height;

            if (provNum > 0 && provNum < counts.Length)
            {
                sumX[provNum] += nx;
                sumY[provNum] += ny;
                counts[provNum]++;
            }

            float elev;
            if (provNum > 0)
            {
                elev = ComputeFeatureAwareElevation(tId, nx, ny, distToCoastPx[i], features);
            }
            else if (tId == WastelandTerrainId)
            {
                var ridge = MathF.Abs(PontariaMapGenerator.Fbm2D(nx * 24.0f + 5.1f, ny * 21.0f + 3.2f));
                elev = Math.Clamp(0.54f + 0.34f * ridge, 0.25f, 0.96f);
            }
            else
            {
                var shelf = Math.Clamp(1.0f - distToCoastPx[i] / 26.0f, 0f, 1f);
                elev = shelf * 0.45f;
            }

            var coastProx = (provNum == 0 && tId != WastelandTerrainId)
                ? Math.Clamp(1.0f - distToCoastPx[i] / 12.0f, 0f, 1f)
                : Math.Clamp(1.0f - distToCoastPx[i] / 10.0f, 0f, 1f);
            var parchmentGrain = 0.5f + 0.45f * PontariaMapGenerator.Fbm2D(nx * 46.0f + 2.3f, ny * 34.0f + 7.1f);

            heightCoastRgb[i * 3 + 0] = (byte)Math.Clamp((int)(elev * 255f), 0, 255);
            heightCoastRgb[i * 3 + 1] = (byte)Math.Clamp((int)(coastProx * 255f), 0, 255);
            heightCoastRgb[i * 3 + 2] = (byte)Math.Clamp((int)(parchmentGrain * 255f), 0, 255);
        }

        // 4. Compute Centroids & Label Anchors
        var provCentroids = new Dictionary<int, (float X, float Y)>(provinces.Count);
        var stateAccum = new Dictionary<string, (double X, double Y, int Count)>(StringComparer.Ordinal);
        var nationAccum = new Dictionary<string, (double X, double Y, float MinX, float MaxX, int Count)>(StringComparer.Ordinal);

        foreach (var p in provinces)
        {
            var c = p.NumericalIndex < counts.Length ? counts[p.NumericalIndex] : 0;
            var cx = c > 0 ? (float)(sumX[p.NumericalIndex] / c) : p.AnchorX;
            var cy = c > 0 ? (float)(sumY[p.NumericalIndex] / c) : p.AnchorY;
            provCentroids[p.NumericalIndex] = (cx, cy);

            var st = stateAccum.GetValueOrDefault(p.StateId);
            stateAccum[p.StateId] = (st.X + cx, st.Y + cy, st.Count + 1);

            var na = nationAccum.TryGetValue(p.OwnerNationId, out var curN)
                ? curN
                : (0.0, 0.0, 1.0f, 0.0f, 0);
            nationAccum[p.OwnerNationId] = (
                na.Item1 + cx * Math.Max(1, c),
                na.Item2 + cy * Math.Max(1, c),
                MathF.Min(na.Item3, cx),
                MathF.Max(na.Item4, cx),
                na.Item5 + Math.Max(1, c));
        }

        var stateCentroids = new Dictionary<string, (float X, float Y)>(stateAccum.Count, StringComparer.Ordinal);
        foreach (var kv in stateAccum)
            stateCentroids[kv.Key] = ((float)(kv.Value.X / kv.Value.Count), (float)(kv.Value.Y / kv.Value.Count));

        var nationAnchors = new Dictionary<string, (float X, float Y, float SpanWidth)>(nationAccum.Count, StringComparer.Ordinal);
        foreach (var kv in nationAccum)
        {
            var span = Math.Clamp(kv.Value.MaxX - kv.Value.MinX, 0.04f, 0.42f);
            nationAnchors[kv.Key] = (
                (float)(kv.Value.X / Math.Max(1, kv.Value.Count)),
                (float)(kv.Value.Y / Math.Max(1, kv.Value.Count)),
                span);
        }

        // 5. Build Batched Shared-Arc Border ArrayMesh data
        var (borderVerts, borderIndices) = BuildBorderRibbonMesh(topology, provinces);
        var hash = ComputeTopologyHash(topology, features, width, height);

        return new BakedMapArtifact(
            width,
            height,
            provinceGrid,
            terrainGrid,
            provinceDataRgb,
            heightCoastRgb,
            borderVerts,
            borderIndices,
            provCentroids,
            stateCentroids,
            nationAnchors,
            hash);
    }

    public static (BorderRibbonVertex[] Vertices, int[] Indices) BuildBorderRibbonMesh(
        PlanarMapTopology topology,
        IReadOnlyList<ProvinceSnapshot> provinces)
    {
        var stateByFace = new Dictionary<int, string>(provinces.Count);
        var nationByFace = new Dictionary<int, string>(provinces.Count);
        foreach (var p in provinces)
        {
            stateByFace[p.NumericalIndex] = p.StateId;
            nationByFace[p.NumericalIndex] = p.OwnerNationId;
        }

        var verts = new List<BorderRibbonVertex>(topology.Arcs.Count * 12);
        var indices = new List<int>(topology.Arcs.Count * 18);

        foreach (var arc in topology.Arcs)
        {
            var pts = arc.Points;
            if (pts.Length < 2)
                continue;

            byte flags = 0;
            if (arc.LeftFaceId == 0 || arc.RightFaceId == 0)
                flags |= 1; // coastline
            else
            {
                var nL = nationByFace.GetValueOrDefault(arc.LeftFaceId, "");
                var nR = nationByFace.GetValueOrDefault(arc.RightFaceId, "");
                var sL = stateByFace.GetValueOrDefault(arc.LeftFaceId, "");
                var sR = stateByFace.GetValueOrDefault(arc.RightFaceId, "");
                if (!string.Equals(nL, nR, StringComparison.Ordinal))
                    flags |= 2; // national border
                else if (!string.Equals(sL, sR, StringComparison.Ordinal))
                    flags |= 4; // state/area border
                else
                    flags |= 8; // province border
            }

            var baseVert = verts.Count;
            for (var i = 0; i < pts.Length; i++)
            {
                float tx, ty;
                if (i == 0)
                {
                    tx = pts[1].X - pts[0].X;
                    ty = pts[1].Y - pts[0].Y;
                }
                else if (i == pts.Length - 1)
                {
                    tx = pts[i].X - pts[i - 1].X;
                    ty = pts[i].Y - pts[i - 1].Y;
                }
                else
                {
                    tx = pts[i + 1].X - pts[i - 1].X;
                    ty = pts[i + 1].Y - pts[i - 1].Y;
                }

                var len = MathF.Sqrt(tx * tx + ty * ty);
                var nx = len > 1e-6f ? -ty / len : 0f;
                var ny = len > 1e-6f ? tx / len : 1f;

                verts.Add(new BorderRibbonVertex(pts[i].X, pts[i].Y, -nx, -ny, arc.LeftFaceId, arc.RightFaceId, flags));
                verts.Add(new BorderRibbonVertex(pts[i].X, pts[i].Y, nx, ny, arc.LeftFaceId, arc.RightFaceId, flags));
            }

            for (var i = 0; i < pts.Length - 1; i++)
            {
                var v0 = baseVert + i * 2;
                var v1 = v0 + 1;
                var v2 = v0 + 2;
                var v3 = v0 + 3;
                indices.Add(v0);
                indices.Add(v2);
                indices.Add(v1);
                indices.Add(v1);
                indices.Add(v2);
                indices.Add(v3);
            }
        }

        return (verts.ToArray(), indices.ToArray());
    }

    private static float[] ComputeDualPassChamferCoastDistance(ushort[] provinceGrid, byte[] terrainGrid, int w, int h)
    {
        var count = w * h;
        var dist = new float[count];
        const float inf = 9999f;

        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var idx = row + x;
                var isLand = provinceGrid[idx] > 0 || terrainGrid[idx] == WastelandTerrainId;
                var onEdge = (x > 0 && ((provinceGrid[idx - 1] > 0 || terrainGrid[idx - 1] == WastelandTerrainId) != isLand)) ||
                             (x < w - 1 && ((provinceGrid[idx + 1] > 0 || terrainGrid[idx + 1] == WastelandTerrainId) != isLand)) ||
                             (y > 0 && ((provinceGrid[idx - w] > 0 || terrainGrid[idx - w] == WastelandTerrainId) != isLand)) ||
                             (y < h - 1 && ((provinceGrid[idx + w] > 0 || terrainGrid[idx + w] == WastelandTerrainId) != isLand));
                dist[idx] = onEdge ? 0f : inf;
            }
        }

        // Forward pass
        const float ortho = 1.0f;
        const float diag = 1.4142f;
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var idx = row + x;
                var d = dist[idx];
                if (d == 0f) continue;

                if (x > 0) d = MathF.Min(d, dist[idx - 1] + ortho);
                if (y > 0)
                {
                    d = MathF.Min(d, dist[idx - w] + ortho);
                    if (x > 0) d = MathF.Min(d, dist[idx - w - 1] + diag);
                    if (x < w - 1) d = MathF.Min(d, dist[idx - w + 1] + diag);
                }
                dist[idx] = d;
            }
        }

        // Backward pass
        for (var y = h - 1; y >= 0; y--)
        {
            var row = y * w;
            for (var x = w - 1; x >= 0; x--)
            {
                var idx = row + x;
                var d = dist[idx];
                if (d == 0f) continue;

                if (x < w - 1) d = MathF.Min(d, dist[idx + 1] + ortho);
                if (y < h - 1)
                {
                    d = MathF.Min(d, dist[idx + w] + ortho);
                    if (x > 0) d = MathF.Min(d, dist[idx + w - 1] + diag);
                    if (x < w - 1) d = MathF.Min(d, dist[idx + w + 1] + diag);
                }
                dist[idx] = d;
            }
        }

        return dist;
    }

    private static float ComputeFeatureAwareElevation(
        byte terrainId,
        float nx,
        float ny,
        float coastDistPx,
        GeographyFeaturesData features)
    {
        var n1 = PontariaMapGenerator.Fbm2D(nx * 18.0f + 5.1f, ny * 14.0f + 2.3f);
        var ridge = 1.0f - MathF.Abs(PontariaMapGenerator.Fbm2D(nx * 30.0f + 1.1f, ny * 24.0f + 8.9f));

        var baseElev = terrainId switch
        {
            5 => 0.72f + ridge * 0.25f,
            4 => 0.56f + n1 * 0.16f + ridge * 0.08f,
            3 => 0.42f + n1 * 0.10f,
            2 => 0.34f + n1 * 0.05f,
            6 => 0.26f + n1 * 0.04f,
            7 => 0.44f + n1 * 0.12f,
            8 => 0.28f + n1 * 0.06f,
            _ => 0.37f + n1 * 0.08f
        };

        foreach (var mr in features.MountainRanges)
        {
            var d = MapWorldGeometrySampler.DistanceToPolyline(nx, ny, mr.SpinePoints);
            var w = Math.Max(0.008f, mr.Width);
            if (d < w)
            {
                var t = 1f - d / w;
                baseElev = MathF.Max(baseElev, 0.55f + t * t * (mr.PeakElevation - 0.55f) + ridge * 0.08f);
            }
        }

        // Gently taper right at the shoreline for crisp coast dropoff
        var coastTaper = Math.Clamp(coastDistPx / 5.5f, 0.35f, 1.0f);
        return Math.Clamp(baseElev * (0.78f + 0.22f * coastTaper), 0.12f, 0.99f);
    }

    public static string ComputeTopologyHash(
        PlanarMapTopology topology,
        GeographyFeaturesData? features,
        int width,
        int height)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder(2048);
        sb.Append(width).Append('x').Append(height).Append('|')
          .Append(topology.Faces.Count).Append('|')
          .Append(topology.Arcs.Count).Append('|');

        foreach (var f in topology.Faces)
            sb.Append(f.FaceId).Append(':').Append(f.Terrain).Append(':').Append(f.OuterRing.Length).Append(';');
        foreach (var a in topology.Arcs)
        {
            sb.Append(a.Id).Append(':').Append(a.LeftFaceId).Append('-').Append(a.RightFaceId);
            foreach (var p in a.Points)
                sb.Append(',').Append((int)(p.X * 10000)).Append('/').Append((int)(p.Y * 10000));
            sb.Append(';');
        }
        if (features is not null)
        {
            var wasteVerts = 0;
            foreach (var lm in features.Landmasses)
            {
                if (string.Equals(lm.Kind, "wasteland_continent", StringComparison.Ordinal))
                    wasteVerts += lm.PolygonPoints.Length;
            }
            sb.Append("F:").Append(features.MountainRanges.Length).Append(',').Append(features.Rivers.Length)
              .Append(',').Append(features.Landmasses.Length).Append(',').Append(wasteVerts);
        }

        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    public static void SaveVmapCache(string filePath, BakedMapArtifact artifact)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using var fs = File.Create(filePath);
        using var gz = new DeflateStream(fs, CompressionLevel.Fastest);
        using var bw = new BinaryWriter(gz, Encoding.UTF8);

        bw.Write("VMAP02"u8);
        bw.Write(artifact.TopologyHash);
        bw.Write(artifact.Width);
        bw.Write(artifact.Height);

        var count = artifact.Width * artifact.Height;
        for (var i = 0; i < count; i++)
            bw.Write(artifact.ProvinceIdGrid[i]);
        bw.Write(artifact.TerrainGrid);
        bw.Write(artifact.ProvinceDataRgb);
        bw.Write(artifact.HeightCoastRgb);

        bw.Write(artifact.BorderVertices.Length);
        foreach (var v in artifact.BorderVertices)
        {
            bw.Write(v.X);
            bw.Write(v.Y);
            bw.Write(v.OffsetX);
            bw.Write(v.OffsetY);
            bw.Write(v.LeftFaceId);
            bw.Write(v.RightFaceId);
            bw.Write(v.BorderFlags);
        }

        bw.Write(artifact.BorderIndices.Length);
        foreach (var idx in artifact.BorderIndices)
            bw.Write(idx);

        bw.Write(artifact.ProvinceCentroids.Count);
        foreach (var kv in artifact.ProvinceCentroids)
        {
            bw.Write(kv.Key);
            bw.Write(kv.Value.X);
            bw.Write(kv.Value.Y);
        }

        bw.Write(artifact.StateCentroids.Count);
        foreach (var kv in artifact.StateCentroids)
        {
            bw.Write(kv.Key);
            bw.Write(kv.Value.X);
            bw.Write(kv.Value.Y);
        }

        bw.Write(artifact.NationLabelAnchors.Count);
        foreach (var kv in artifact.NationLabelAnchors)
        {
            bw.Write(kv.Key);
            bw.Write(kv.Value.X);
            bw.Write(kv.Value.Y);
            bw.Write(kv.Value.SpanWidth);
        }
    }

    public static BakedMapArtifact? TryLoadVmapCache(string filePath, string expectedHash)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            using var fs = File.OpenRead(filePath);
            using var gz = new DeflateStream(fs, CompressionMode.Decompress);
            using var br = new BinaryReader(gz, Encoding.UTF8);

            var magic = Encoding.UTF8.GetString(br.ReadBytes(6));
            if (magic != "VMAP02")
                return null;

            var hash = br.ReadString();
            if (!string.Equals(hash, expectedHash, StringComparison.Ordinal))
                return null;

            var width = br.ReadInt32();
            var height = br.ReadInt32();
            var count = width * height;

            var provGrid = new ushort[count];
            for (var i = 0; i < count; i++)
                provGrid[i] = br.ReadUInt16();

            var terrainGrid = br.ReadBytes(count);
            var pData = br.ReadBytes(count * 3);
            var hData = br.ReadBytes(count * 3);

            var vLen = br.ReadInt32();
            var verts = new BorderRibbonVertex[vLen];
            for (var i = 0; i < vLen; i++)
                verts[i] = new BorderRibbonVertex(
                    br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
                    br.ReadInt32(), br.ReadInt32(), br.ReadByte());

            var iLen = br.ReadInt32();
            var indices = new int[iLen];
            for (var i = 0; i < iLen; i++)
                indices[i] = br.ReadInt32();

            var pcLen = br.ReadInt32();
            var provCentroids = new Dictionary<int, (float, float)>(pcLen);
            for (var i = 0; i < pcLen; i++)
                provCentroids[br.ReadInt32()] = (br.ReadSingle(), br.ReadSingle());

            var scLen = br.ReadInt32();
            var stateCentroids = new Dictionary<string, (float, float)>(scLen, StringComparer.Ordinal);
            for (var i = 0; i < scLen; i++)
                stateCentroids[br.ReadString()] = (br.ReadSingle(), br.ReadSingle());

            var ncLen = br.ReadInt32();
            var nationAnchors = new Dictionary<string, (float, float, float)>(ncLen, StringComparer.Ordinal);
            for (var i = 0; i < ncLen; i++)
                nationAnchors[br.ReadString()] = (br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

            return new BakedMapArtifact(
                width, height, provGrid, terrainGrid, pData, hData,
                verts, indices, provCentroids, stateCentroids, nationAnchors, hash);
        }
        catch
        {
            return null;
        }
    }
}
