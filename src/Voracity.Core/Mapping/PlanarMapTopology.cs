using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using QuikGraph;
using Voracity.Core.Models;

namespace Voracity.Core.Mapping;

public readonly record struct TopoVertex(int Id, float X, float Y);

/// <summary>
/// Shared border polyline between two planar faces (0 = Outside Void).
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
/// Closed territorial polygon face tied 1:1 to a Location's PersistentUid (NumericalIndex).
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
    string[] AdjacentProvinceIds,
    LocationKind Kind = LocationKind.Habitable);

/// <summary>
/// Preserves provenance when a location/province is merged/retired in the Map Editor.
/// </summary>
public sealed record ProvinceTombstone(
    int RetiredPersistentUid,
    string RetiredProvinceId,
    string MergedIntoProvinceId,
    long TimestampUnixSeconds);

/// <summary>
/// Authoritative planar topological representation of the world map backed by QuikGraph + NetTopologySuite STRtree.
/// </summary>
public sealed class PlanarMapTopology
{
    private static readonly GeometryFactory GeomFactory = GeometryFactory.Default;

    public PlanarMapTopology(
        List<TopoVertex> vertices,
        List<SharedArcRecord> arcs,
        List<FaceRecord> faces,
        List<(int ProvA, int ProvB)> symmetricProvinceAdjacencies,
        List<(string StateA, string StateB)> symmetricStateAdjacencies,
        List<ProvinceTombstone>? tombstones = null,
        IReadOnlyList<(int FaceA, int FaceB, LocationEdgeInfo Info)>? typedEdges = null)
    {
        Vertices = vertices;
        Arcs = arcs;
        Faces = faces;
        SymmetricProvinceAdjacencies = symmetricProvinceAdjacencies;
        SymmetricStateAdjacencies = symmetricStateAdjacencies;
        Tombstones = tombstones ?? new List<ProvinceTombstone>();
        TypedEdges = typedEdges ?? Array.Empty<(int, int, LocationEdgeInfo)>();

        RebuildIndices();
    }

    public List<TopoVertex> Vertices { get; }
    public List<SharedArcRecord> Arcs { get; }
    public List<FaceRecord> Faces { get; }
    public List<(int ProvA, int ProvB)> SymmetricProvinceAdjacencies { get; }
    public List<(string StateA, string StateB)> SymmetricStateAdjacencies { get; }
    public List<ProvinceTombstone> Tombstones { get; }
    public IReadOnlyList<(int FaceA, int FaceB, LocationEdgeInfo Info)> TypedEdges { get; }

    public Dictionary<int, TopoVertex> VertexById { get; } = new();
    public Dictionary<int, FaceRecord> FaceById { get; } = new();
    public Dictionary<string, FaceRecord> FaceByProvinceId { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, List<int>> NeighborFaceIds { get; } = new();

    public UndirectedGraph<int, TaggedEdge<int, LocationEdgeInfo>> AdjacencyGraph { get; private set; } =
        new(allowParallelEdges: false);

    public STRtree<int> SpatialIndex { get; private set; } = new();

    public void RebuildIndices()
    {
        VertexById.Clear();
        foreach (var v in Vertices)
            VertexById[v.Id] = v;

        FaceById.Clear();
        FaceByProvinceId.Clear();
        NeighborFaceIds.Clear();

        var graph = new UndirectedGraph<int, TaggedEdge<int, LocationEdgeInfo>>(allowParallelEdges: false);
        var tree = new STRtree<int>();

        foreach (var f in Faces)
        {
            FaceById[f.FaceId] = f;
            FaceByProvinceId[f.ProvinceId] = f;
            NeighborFaceIds[f.FaceId] = new List<int>();
            graph.AddVertex(f.FaceId);

            if (f.OuterRing.Length >= 3)
            {
                var minX = float.MaxValue;
                var maxX = float.MinValue;
                var minY = float.MaxValue;
                var maxY = float.MinValue;
                for (var i = 0; i < f.OuterRing.Length; i++)
                {
                    var pt = f.OuterRing[i];
                    if (pt.X < minX) minX = pt.X;
                    if (pt.X > maxX) maxX = pt.X;
                    if (pt.Y < minY) minY = pt.Y;
                    if (pt.Y > maxY) maxY = pt.Y;
                }
                tree.Insert(new Envelope(minX, maxX, minY, maxY), f.FaceId);
            }
        }

        tree.Build();
        SpatialIndex = tree;

        var addedPairs = new HashSet<(int, int)>();
        foreach (var (a, b, info) in TypedEdges)
        {
            if (a == b || !FaceById.ContainsKey(a) || !FaceById.ContainsKey(b))
                continue;
            var key = a < b ? (a, b) : (b, a);
            if (addedPairs.Add(key))
                graph.AddEdge(new TaggedEdge<int, LocationEdgeInfo>(key.Item1, key.Item2, info));

            if (NeighborFaceIds.TryGetValue(a, out var listA) && !listA.Contains(b))
                listA.Add(b);
            if (NeighborFaceIds.TryGetValue(b, out var listB) && !listB.Contains(a))
                listB.Add(a);
        }

        foreach (var (a, b) in SymmetricProvinceAdjacencies)
        {
            if (a == b || !FaceById.ContainsKey(a) || !FaceById.ContainsKey(b))
                continue;

            var key = a < b ? (a, b) : (b, a);
            if (addedPairs.Add(key))
            {
                var fa = FaceById[a];
                var fb = FaceById[b];
                var edgeKind = (fa.Kind == LocationKind.SeaZone && fb.Kind == LocationKind.SeaZone)
                    ? LocationEdgeKind.SeaSea
                    : (fa.Kind == LocationKind.SeaZone || fb.Kind == LocationKind.SeaZone)
                        ? LocationEdgeKind.CoastSea
                        : LocationEdgeKind.LandLand;
                graph.AddEdge(new TaggedEdge<int, LocationEdgeInfo>(key.Item1, key.Item2, new LocationEdgeInfo(edgeKind)));
            }

            if (NeighborFaceIds.TryGetValue(a, out var listA) && !listA.Contains(b))
                listA.Add(b);
            if (NeighborFaceIds.TryGetValue(b, out var listB) && !listB.Contains(a))
                listB.Add(a);
        }

        AdjacencyGraph = graph;
    }

    public int HitTestFace(float normX, float normY)
    {
        var env = new Envelope(normX, normX, normY, normY);
        var candidates = SpatialIndex.Query(env);
        for (var i = 0; i < candidates.Count; i++)
        {
            var fid = candidates[i];
            if (FaceById.TryGetValue(fid, out var face) && PointInPolygon(face.OuterRing, normX, normY))
                return fid;
        }
        return 0;
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

        var coords = new Coordinate[ring.Length + 1];
        for (var i = 0; i < ring.Length; i++)
            coords[i] = new Coordinate(ring[i].X, ring[i].Y);
        coords[ring.Length] = coords[0];

        return (float)GeomFactory.CreatePolygon(coords).Area;
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
            var hit = _topology.HitTestFace(normX, normY);
            return hit == 0 || (_topology.FaceById.TryGetValue(hit, out var f) && f.Kind == LocationKind.SeaZone);
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

    internal static float DistanceToPolyline(float px, float py, Point2D[] spine)
    {
        if (spine.Length == 0)
            return float.MaxValue;
        if (spine.Length == 1)
        {
            var dx = px - spine[0].X;
            var dy = py - spine[0].Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        var best = float.MaxValue;
        for (var i = 0; i < spine.Length - 1; i++)
        {
            var d = DistanceToSegment(px, py, spine[i].X, spine[i].Y, spine[i + 1].X, spine[i + 1].Y);
            if (d < best)
                best = d;
        }
        return best;
    }

    private static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
    {
        var abx = bx - ax;
        var aby = by - ay;
        var lenSq = abx * abx + aby * aby;
        if (lenSq <= 1e-8f)
        {
            var dx = px - ax;
            var dy = py - ay;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        var t = Math.Clamp(((px - ax) * abx + (py - ay) * aby) / lenSq, 0f, 1f);
        var cx = ax + t * abx;
        var cy = ay + t * aby;
        var ddx = px - cx;
        var ddy = py - cy;
        return MathF.Sqrt(ddx * ddx + ddy * ddy);
    }
}
