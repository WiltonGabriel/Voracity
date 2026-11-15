using Voracity.Core.Models;

namespace Voracity.Core.Mapping;

public readonly record struct TopoVertex(int Id, float X, float Y);

/// <summary>
/// Shared border polyline between two planar faces (0 = Ocean/Void).
/// Dragging an interior or endpoint vertex updates both LeftFaceId and RightFaceId identically without cracks.
/// </summary>
public sealed record SharedArcRecord(
    int Id,
    int LeftFaceId,
    int RightFaceId,
    int StartVertexId,
    int EndVertexId,
    Point2D[] Points,
    bool IsCoastline,
    bool IsInternationalBorder);

/// <summary>
/// Closed territorial polygon face tied 1:1 to a Province's PersistentUid (NumericalIndex).
/// </summary>
public sealed record FaceRecord(
    int FaceId,
    int PersistentUid,
    string ProvinceId,
    string StateId,
    string OwnerNationId,
    string Terrain,
    float SeedX,
    float SeedY,
    float AreaNormalized,
    bool IsCoastal,
    int[] ArcIds,
    Point2D[] OuterRing,
    string[] AdjacentProvinceIds);

/// <summary>
/// Preserves provenance when a province is merged/retired in the Map Editor so saves & references remain trackable.
/// </summary>
public sealed record ProvinceTombstone(
    int RetiredPersistentUid,
    string RetiredProvinceId,
    string MergedIntoProvinceId,
    long TimestampUnixSeconds);

/// <summary>
/// Authoritative planar topological representation of the world map.
/// Raster ID textures and batched border meshes are strictly derived from this topology.
/// </summary>
public sealed class PlanarMapTopology
{
    public PlanarMapTopology(
        List<TopoVertex> vertices,
        List<SharedArcRecord> arcs,
        List<FaceRecord> faces,
        List<(int ProvA, int ProvB)> symmetricProvinceAdjacencies,
        List<(string StateA, string StateB)> symmetricStateAdjacencies,
        List<ProvinceTombstone>? tombstones = null)
    {
        Vertices = vertices;
        Arcs = arcs;
        Faces = faces;
        SymmetricProvinceAdjacencies = symmetricProvinceAdjacencies;
        SymmetricStateAdjacencies = symmetricStateAdjacencies;
        Tombstones = tombstones ?? new List<ProvinceTombstone>();

        RebuildIndices();
    }

    public List<TopoVertex> Vertices { get; }
    public List<SharedArcRecord> Arcs { get; }
    public List<FaceRecord> Faces { get; }
    public List<(int ProvA, int ProvB)> SymmetricProvinceAdjacencies { get; }
    public List<(string StateA, string StateB)> SymmetricStateAdjacencies { get; }
    public List<ProvinceTombstone> Tombstones { get; }

    public Dictionary<int, TopoVertex> VertexById { get; } = new();
    public Dictionary<int, FaceRecord> FaceById { get; } = new();
    public Dictionary<string, FaceRecord> FaceByProvinceId { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, List<int>> NeighborFaceIds { get; } = new();

    public void RebuildIndices()
    {
        VertexById.Clear();
        foreach (var v in Vertices)
            VertexById[v.Id] = v;

        FaceById.Clear();
        FaceByProvinceId.Clear();
        NeighborFaceIds.Clear();

        foreach (var f in Faces)
        {
            FaceById[f.FaceId] = f;
            FaceByProvinceId[f.ProvinceId] = f;
            NeighborFaceIds[f.FaceId] = new List<int>();
        }

        foreach (var (a, b) in SymmetricProvinceAdjacencies)
        {
            if (NeighborFaceIds.TryGetValue(a, out var listA) && !listA.Contains(b))
                listA.Add(b);
            if (NeighborFaceIds.TryGetValue(b, out var listB) && !listB.Contains(a))
                listB.Add(a);
        }
    }

    public static bool PointInPolygon(Point2D[] ring, float x, float y)
    {
        if (ring.Length < 3)
            return false;

        var inside = false;
        var j = ring.Length - 1;
        for (var i = 0; i < ring.Length; i++)
        {
            var xi = ring[i].X;
            var yi = ring[i].Y;
            var xj = ring[j].X;
            var yj = ring[j].Y;

            var intersect = ((yi > y) != (yj > y)) &&
                            (x < (xj - xi) * (y - yi) / ((yj - yi) == 0f ? 1e-7f : (yj - yi)) + xi);
            if (intersect)
                inside = !inside;
            j = i;
        }
        return inside;
    }

    public static float ComputePolygonArea(Point2D[] ring)
    {
        if (ring.Length < 3)
            return 0f;

        double sum = 0.0;
        for (var i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            sum += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        return (float)(Math.Abs(sum) * 0.5);
    }
}

/// <summary>
/// 3D-readiness adapter implementing IMapWorldGeometry over topology + elevation data.
/// </summary>
public sealed class MapWorldGeometrySampler : IMapWorldGeometry
{
    private readonly GeographyFeaturesData _features;
    private readonly PlanarMapTopology? _topology;

    public MapWorldGeometrySampler(
        PlanarMapTopology? topology,
        GeographyFeaturesData? features,
        float worldWidthKm = 5400f,
        float worldHeightKm = 3150f,
        float maxElevationMeters = 6200f)
    {
        _topology = topology;
        _features = features ?? GeographyFeaturesData.Empty;
        WorldWidthKm = worldWidthKm;
        WorldHeightKm = worldHeightKm;
        MaxElevationMeters = maxElevationMeters;
    }

    public float WorldWidthKm { get; }
    public float WorldHeightKm { get; }
    public float MaxElevationMeters { get; }

    public (float XKm, float ZKm) NormalizedToWorldKm(float normX, float normY)
        => (normX * WorldWidthKm, normY * WorldHeightKm);

    public (float NormX, float NormY) WorldKmToNormalized(float xKm, float zKm)
        => (xKm / Math.Max(1f, WorldWidthKm), zKm / Math.Max(1f, WorldHeightKm));

    public bool IsWaterAt(float normX, float normY)
    {
        if (_features.Landmasses.Length > 0)
        {
            var insideMain = false;
            foreach (var lm in _features.Landmasses)
            {
                if (lm.Kind == "inland_sea_mask")
                {
                    if (PlanarMapTopology.PointInPolygon(lm.PolygonPoints, normX, normY))
                        return true;
                }
                else if (PlanarMapTopology.PointInPolygon(lm.PolygonPoints, normX, normY))
                {
                    insideMain = true;
                }
            }
            if (!insideMain)
                return true;

            foreach (var lake in _features.Lakes)
            {
                if (PlanarMapTopology.PointInPolygon(lake.PolygonPoints, normX, normY))
                    return true;
            }
            return false;
        }

        if (_topology is not null)
        {
            foreach (var f in _topology.Faces)
            {
                if (PlanarMapTopology.PointInPolygon(f.OuterRing, normX, normY))
                    return false;
            }
            return true;
        }

        return false;
    }

    public float SampleElevationMeters(float normX, float normY)
    {
        if (IsWaterAt(normX, normY))
            return 0f;

        var baseMeters = 180f;
        foreach (var mr in _features.MountainRanges)
        {
            var d = DistanceToPolyline(normX, normY, mr.SpinePoints);
            var w = Math.Max(0.008f, mr.Width);
            if (d < w)
            {
                var t = 1f - d / w;
                baseMeters = Math.Max(baseMeters, t * t * mr.PeakElevation * MaxElevationMeters);
            }
        }

        return Math.Clamp(baseMeters, 0f, MaxElevationMeters);
    }

    public IReadOnlyList<(string RiverId, Point2D[] PointsKm)> GetRiverSplinesKm()
    {
        var list = new List<(string, Point2D[])>(_features.Rivers.Length);
        foreach (var r in _features.Rivers)
        {
            var pts = new Point2D[r.ControlPoints.Length];
            for (var i = 0; i < r.ControlPoints.Length; i++)
            {
                var (xk, zk) = NormalizedToWorldKm(r.ControlPoints[i].X, r.ControlPoints[i].Y);
                pts[i] = new Point2D(xk, zk);
            }
            list.Add((r.Id, pts));
        }
        return list;
    }

    internal static float DistanceToPolyline(float px, float py, Point2D[] pts)
    {
        if (pts.Length == 0)
            return float.MaxValue;
        if (pts.Length == 1)
        {
            var dx0 = px - pts[0].X;
            var dy0 = py - pts[0].Y;
            return MathF.Sqrt(dx0 * dx0 + dy0 * dy0);
        }

        var minDist = float.MaxValue;
        for (var i = 0; i < pts.Length - 1; i++)
        {
            var ax = pts[i].X;
            var ay = pts[i].Y;
            var bx = pts[i + 1].X;
            var by = pts[i + 1].Y;
            var dx = bx - ax;
            var dy = by - ay;
            var lenSq = dx * dx + dy * dy;
            var t = lenSq > 1e-8f ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0f, 1f) : 0f;
            var cx = ax + dx * t;
            var cy = ay + dy * t;
            var ex = px - cx;
            var ey = py - cy;
            var dist = MathF.Sqrt(ex * ex + ey * ey);
            if (dist < minDist)
                minDist = dist;
        }
        return minDist;
    }
}
