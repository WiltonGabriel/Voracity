using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;
using Voracity.Core.Models;

namespace Voracity.Core.Mapping;

public sealed record TopologyBuildOptions(
    int GridWidth = 0,
    int GridHeight = 0,
    bool IncludeSeaZones = true,
    bool IncludeSpecialLocations = true)
{
    public static TopologyBuildOptions Default { get; } = new();

    public (int Width, int Height) ResolveGridSize(int locationCount)
    {
        if (GridWidth > 0 && GridHeight > 0)
            return (GridWidth, GridHeight);
        if (locationCount > 600)
            return (1080, 630);
        if (locationCount > 250)
            return (720, 420);
        return (540, 315);
    }
}

/// <summary>
/// Builds a watertight planar vector topology (Vertices, SharedArcs, Faces) from Location definitions + landmass contours.
/// Supports Habitable, Impassable, Corridor, and SeaZone Locations with zero per-corner heap HashSets,
/// scanline pre-rasterized land/sea/wasteland masks, NetTopologySuite Douglas-Peucker simplification,
/// QuikGraph typed edges, and 100% topology-derived IsCoastal flags.
/// </summary>
public static class PlanarTopologyBuilder
{
    public const int TopoGridWidth = 540;
    public const int TopoGridHeight = 315;
    private const float AspectX = 1.714f;
    private static readonly GeometryFactory GeomFactory = GeometryFactory.Default;

    public static PlanarMapTopology BuildFromProvinces(
        IReadOnlyList<ProvinceSnapshot> provinces,
        GeographyFeaturesData? features = null,
        int gridWidth = 0,
        int gridHeight = 0)
    {
        var locations = new LocationDefinition[provinces.Count];
        for (var i = 0; i < provinces.Count; i++)
        {
            var p = provinces[i];
            locations[i] = new LocationDefinition(
                Id: p.ProvinceId,
                NameKey: $"loc.{p.ProvinceId}",
                DefaultName: p.Name,
                Kind: p.Kind,
                ProvinceId: p.ProvinceId,
                AreaId: p.StateId,
                RegionId: string.Empty,
                SuperRegionId: string.Empty,
                ContinentId: "pontaria",
                OwnerNationId: p.OwnerNationId,
                Rank: p.Rank,
                Topography: p.Topography,
                Vegetation: p.Vegetation,
                Climate: p.Climate,
                RawGoodId: p.ResourcePotential,
                RgoMethod: RgoMethod.Farming,
                NaturalHarborSuitability: p.IsCoastal ? 50 : 15,
                Development: 6.0,
                RoadLevel: RoadLevel.None,
                InitialPopulation: 1000,
                SeedX: p.AnchorX,
                SeedY: p.AnchorY,
                BasePopulationCapacity: 42.0,
                IsCapital: p.IsCapital,
                IsMarketCenter: false,
                MarketId: null,
                SeaZoneMaritimePresence: 0.0,
                PersistentUid: p.NumericalIndex);
        }

        return BuildFromLocations(locations, features, new TopologyBuildOptions(gridWidth, gridHeight));
    }

    public static PlanarMapTopology BuildFromLocations(
        IReadOnlyList<LocationDefinition> locations,
        GeographyFeaturesData? features = null,
        TopologyBuildOptions? options = null,
        IReadOnlyDictionary<string, string[]>? corridorConnections = null)
    {
        features ??= GeographyFeaturesData.Empty;
        options ??= TopologyBuildOptions.Default;

        var (w, h) = options.ResolveGridSize(locations.Count);
        var locByFaceId = new Dictionary<int, LocationDefinition>(locations.Count);
        var locById = new Dictionary<string, LocationDefinition>(locations.Count, StringComparer.Ordinal);
        var landSeeds = new SpatialGrid2D<int>(64, 36);
        var wastelandSeeds = new SpatialGrid2D<int>(24, 16);
        var seaSeeds = new SpatialGrid2D<int>(32, 20);

        var landList = new List<LocationDefinition>(locations.Count);
        var wastelandList = new List<LocationDefinition>();
        var seaList = new List<LocationDefinition>();

        for (var i = 0; i < locations.Count; i++)
        {
            var loc = locations[i];
            var fid = loc.PersistentUid > 0 ? loc.PersistentUid : i + 1;
            if (locByFaceId.ContainsKey(fid))
                continue;

            loc = loc with { PersistentUid = fid };
            locByFaceId[fid] = loc;
            locById[loc.Id] = loc;

            switch (loc.Kind)
            {
                case LocationKind.SeaZone:
                    if (options.IncludeSeaZones)
                    {
                        seaSeeds.Add(loc.SeedX, loc.SeedY, fid);
                        seaList.Add(loc);
                    }
                    break;
                case LocationKind.Impassable:
                    if (options.IncludeSpecialLocations)
                    {
                        if (IsPointInsideWastelandContours(loc.SeedX, loc.SeedY, features))
                        {
                            wastelandSeeds.Add(loc.SeedX, loc.SeedY, fid);
                            wastelandList.Add(loc);
                        }
                        else
                        {
                            landSeeds.Add(loc.SeedX, loc.SeedY, fid);
                            landList.Add(loc);
                        }
                    }
                    break;
                default:
                    if (options.IncludeSpecialLocations || loc.Kind == LocationKind.Habitable)
                    {
                        landSeeds.Add(loc.SeedX, loc.SeedY, fid);
                        landList.Add(loc);
                    }
                    break;
            }
        }

        // Step 1: Pre-rasterize Land/Wasteland/Sea mask once in O(polygons * h) scanlines
        var maskGrid = new byte[w * h]; // 0 = Sea, 1 = Land, 2 = Wasteland
        var hasLandmasses = features.Landmasses.Length > 0;
        if (hasLandmasses)
        {
            BuildPreRasterizedMask(maskGrid, w, h, features);
        }
        else
        {
            RasterizeMetaballMask(maskGrid, w, h, landList, seaList, landSeeds);
        }

        // Step 2: Assign every grid cell via domain-warped Voronoi strictly partitioned by Mask
        var idGrid = new int[w * h];
        var hasSeaZones = seaSeeds.Count > 0;
        var hasWastelandSeeds = wastelandSeeds.Count > 0;

        for (var y = 0; y < h; y++)
        {
            var ny = (y + 0.5f) / h;
            var rowOffset = y * w;

            for (var x = 0; x < w; x++)
            {
                var nx = (x + 0.5f) / w;
                var idx = rowOffset + x;
                var mask = maskGrid[idx];

                var wx = nx + MapTerrainNoise.Fbm2D(nx * 11.5f + 3.1f, ny * 8.2f + 1.7f) * 0.018f;
                var wy = ny + MapTerrainNoise.Fbm2D(nx * 10.8f + 9.4f, ny * 9.1f + 5.3f) * 0.016f;

                if (mask == 1)
                {
                    var slot = landSeeds.FindNearest(wx, wy, AspectX, 0.35f);
                    if (slot >= 0)
                        idGrid[idx] = landSeeds.GetAt(slot).Item;
                }
                else if (mask == 2)
                {
                    if (hasWastelandSeeds)
                    {
                        var slot = wastelandSeeds.FindNearest(wx, wy, AspectX, 0.50f);
                        if (slot >= 0)
                            idGrid[idx] = wastelandSeeds.GetAt(slot).Item;
                    }
                }
                else if (hasSeaZones)
                {
                    var slot = seaSeeds.FindNearest(wx, wy, AspectX, 0.65f);
                    if (slot >= 0)
                        idGrid[idx] = seaSeeds.GetAt(slot).Item;
                }
            }
        }

        // Step 3: Guarantee every seeded location owns at least its immediate anchor cell(s) without overwriting another anchor
        var protectedCells = new bool[w * h];
        EnsureAllSeedsRepresented(landList, idGrid, maskGrid, protectedCells, expectedMask: 1, w, h);
        EnsureAllSeedsRepresented(wastelandList, idGrid, maskGrid, protectedCells, expectedMask: 2, w, h);
        EnsureAllSeedsRepresented(seaList, idGrid, maskGrid, protectedCells, expectedMask: 0, w, h);

        return ExtractTopologyFromIdGrid(idGrid, w, h, locByFaceId, locById, features, corridorConnections);
    }

    private static void EnsureAllSeedsRepresented(
        List<LocationDefinition> seeds,
        int[] idGrid,
        byte[] maskGrid,
        bool[] protectedCells,
        byte expectedMask,
        int w,
        int h)
    {
        for (var i = 0; i < seeds.Count; i++)
        {
            var loc = seeds[i];
            var fid = loc.PersistentUid;
            var gx = Math.Clamp((int)(loc.SeedX * w), 1, w - 2);
            var gy = Math.Clamp((int)(loc.SeedY * h), 1, h - 2);

            var hasPixel = false;
            for (var dy = -2; dy <= 2 && !hasPixel; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    if (idGrid[(gy + dy) * w + (gx + dx)] == fid)
                    {
                        hasPixel = true;
                        break;
                    }
                }
            }

            if (!hasPixel)
            {
                idGrid[gy * w + gx] = fid;
                protectedCells[gy * w + gx] = true;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nIdx = (gy + dy) * w + (gx + dx);
                        if (!protectedCells[nIdx] && maskGrid[nIdx] == expectedMask)
                            idGrid[nIdx] = fid;
                    }
                }
            }
            else
            {
                if (idGrid[gy * w + gx] == fid)
                    protectedCells[gy * w + gx] = true;
            }
        }
    }

    private static bool IsPointInsideWastelandContours(float nx, float ny, GeographyFeaturesData features)
    {
        foreach (var lm in features.Landmasses)
        {
            if (string.Equals(lm.Kind, "wasteland_continent", StringComparison.Ordinal)
                && PlanarMapTopology.PointInPolygon(lm.PolygonPoints, nx, ny))
                return true;
        }
        return false;
    }

    private static void BuildPreRasterizedMask(byte[] maskGrid, int w, int h, GeographyFeaturesData features)
    {
        var intersections = new List<float>(32);

        // Pass 1: Continents & islands => mask = 1
        foreach (var lm in features.Landmasses)
        {
            if (string.Equals(lm.Kind, "inland_sea_mask", StringComparison.Ordinal) ||
                string.Equals(lm.Kind, "wasteland_continent", StringComparison.Ordinal) ||
                lm.PolygonPoints.Length < 3)
                continue;

            ScanlineFillMask(lm.PolygonPoints, maskGrid, w, h, value: 1, intersections);
        }

        // Pass 2: Inland seas & lakes => reset to sea (0)
        foreach (var lm in features.Landmasses)
        {
            if (string.Equals(lm.Kind, "inland_sea_mask", StringComparison.Ordinal) && lm.PolygonPoints.Length >= 3)
                ScanlineFillMask(lm.PolygonPoints, maskGrid, w, h, value: 0, intersections);
        }
        foreach (var lake in features.Lakes)
        {
            if (lake.PolygonPoints.Length >= 3)
                ScanlineFillMask(lake.PolygonPoints, maskGrid, w, h, value: 0, intersections);
        }

        // Pass 3: Impassable wasteland continents => mask = 2
        foreach (var lm in features.Landmasses)
        {
            if (string.Equals(lm.Kind, "wasteland_continent", StringComparison.Ordinal) && lm.PolygonPoints.Length >= 3)
                ScanlineFillMask(lm.PolygonPoints, maskGrid, w, h, value: 2, intersections);
        }

        // Clear narrow outer margin frames to Sea (0)
        for (var y = 0; y < h; y++)
        {
            var ny = (y + 0.5f) / h;
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var nx = (x + 0.5f) / w;
                if (nx < 0.02f || nx > 0.985f || ny < 0.02f || ny > 0.98f)
                {
                    if (maskGrid[row + x] != 2)
                        maskGrid[row + x] = 0;
                }
            }
        }
    }

    private static void ScanlineFillMask(Point2D[] ring, byte[] maskGrid, int w, int h, byte value, List<float> intersections)
    {
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        for (var i = 0; i < ring.Length; i++)
        {
            if (ring[i].Y < minY) minY = ring[i].Y;
            if (ring[i].Y > maxY) maxY = ring[i].Y;
        }

        var yStart = Math.Clamp((int)MathF.Floor(minY * h), 0, h - 1);
        var yEnd = Math.Clamp((int)MathF.Ceiling(maxY * h), 0, h - 1);

        for (var py = yStart; py <= yEnd; py++)
        {
            var ny = (py + 0.5f) / h;
            intersections.Clear();

            var j = ring.Length - 1;
            for (var i = 0; i < ring.Length; i++)
            {
                var yi = ring[i].Y;
                var yj = ring[j].Y;
                if ((yi <= ny && yj > ny) || (yj <= ny && yi > ny))
                {
                    var ix = ring[i].X + (ny - yi) / (yj - yi) * (ring[j].X - ring[i].X);
                    intersections.Add(ix);
                }
                j = i;
            }

            if (intersections.Count < 2)
                continue;
            intersections.Sort();

            var rowBase = py * w;
            for (var k = 0; k + 1 < intersections.Count; k += 2)
            {
                var x0 = Math.Clamp((int)MathF.Ceiling(intersections[k] * w - 0.5f), 0, w - 1);
                var x1 = Math.Clamp((int)MathF.Floor(intersections[k + 1] * w - 0.5f), 0, w - 1);
                for (var px = x0; px <= x1; px++)
                    maskGrid[rowBase + px] = value;
            }
        }
    }

    private static void RasterizeMetaballMask(
        byte[] maskGrid,
        int w,
        int h,
        List<LocationDefinition> landSeeds,
        List<LocationDefinition> seaSeeds,
        SpatialGrid2D<int> landSpatial)
    {
        var queryList = new List<int>(24);
        var byFid = new Dictionary<int, LocationDefinition>(landSeeds.Count);
        foreach (var l in landSeeds)
            byFid[l.PersistentUid] = l;

        for (var y = 0; y < h; y++)
        {
            var ny = (y + 0.5f) / h;
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var nx = (x + 0.5f) / w;
                if (nx < 0.02f || nx > 0.98f || ny < 0.025f || ny > 0.975f)
                    continue;

                var wx = nx + MapTerrainNoise.Fbm2D(nx * 11.5f + 3.1f, ny * 8.2f + 1.7f) * 0.024f;
                var wy = ny + MapTerrainNoise.Fbm2D(nx * 10.8f + 9.4f, ny * 9.1f + 5.3f) * 0.022f;

                var potential = 0f;
                var bestDistSq = float.MaxValue;

                if (landSeeds.Count <= 300)
                {
                    for (var i = 0; i < landSeeds.Count; i++)
                    {
                        var s = landSeeds[i];
                        var dx = (wx - s.SeedX) * AspectX;
                        var dy = wy - s.SeedY;
                        var dSq = dx * dx + dy * dy;
                        if (dSq < bestDistSq) bestDistSq = dSq;
                        var contrib = 0.016f / (dSq + 0.0022f);
                        potential = contrib > potential ? contrib : potential + contrib * 0.28f;
                    }
                }
                else
                {
                    landSpatial.QueryRect(wx - 0.18f, wy - 0.18f, wx + 0.18f, wy + 0.18f, queryList);
                    for (var i = 0; i < queryList.Count; i++)
                    {
                        if (!byFid.TryGetValue(queryList[i], out var s))
                            continue;
                        var dx = (wx - s.SeedX) * AspectX;
                        var dy = wy - s.SeedY;
                        var dSq = dx * dx + dy * dy;
                        if (dSq < bestDistSq) bestDistSq = dSq;
                        var contrib = 0.016f / (dSq + 0.0022f);
                        potential = contrib > potential ? contrib : potential + contrib * 0.28f;
                    }
                }

                // If explicit sea seeds exist and a sea seed is closer than land, treat as sea
                var seaCloser = false;
                if (seaSeeds.Count > 0)
                {
                    for (var i = 0; i < seaSeeds.Count; i++)
                    {
                        var ss = seaSeeds[i];
                        var dx = (wx - ss.SeedX) * AspectX;
                        var dy = wy - ss.SeedY;
                        if (dx * dx + dy * dy < bestDistSq * 0.85f)
                        {
                            seaCloser = true;
                            break;
                        }
                    }
                }

                var coastNoise = MapTerrainNoise.Fbm2D(nx * 19.0f + 14.2f, ny * 14.5f + 8.7f) * 0.34f;
                if (!seaCloser && potential + coastNoise >= 1.12f && bestDistSq < 0.036f)
                    maskGrid[row + x] = 1;
            }
        }
    }

    private readonly record struct GridEdge(int StartCorner, int EndCorner, int LeftFace, int RightFace);

    private static int CountDistinctFaces(int a, int b, int c, int d)
    {
        var count = 1;
        if (b != a) count++;
        if (c != a && c != b) count++;
        if (d != a && d != b && d != c) count++;
        return count;
    }

    private static PlanarMapTopology ExtractTopologyFromIdGrid(
        int[] idGrid,
        int w,
        int h,
        Dictionary<int, LocationDefinition> locByFaceId,
        Dictionary<string, LocationDefinition> locById,
        GeographyFeaturesData features,
        IReadOnlyDictionary<string, string[]>? corridorConnections)
    {
        var cw = w + 1;
        var totalCorners = cw * (h + 1);

        int SampleCell(int x, int y)
        {
            if (x < 0 || x >= w || y < 0 || y >= h)
                return 0;
            return idGrid[y * w + x];
        }

        // Compute corner junction flags WITHOUT allocating HashSet<int>[] on heap!
        var cornerDistinctCounts = new byte[totalCorners];
        for (var cy = 0; cy <= h; cy++)
        {
            var row = cy * cw;
            for (var cx = 0; cx <= w; cx++)
            {
                var nw = SampleCell(cx - 1, cy - 1);
                var ne = SampleCell(cx, cy - 1);
                var sw = SampleCell(cx - 1, cy);
                var se = SampleCell(cx, cy);
                if (nw != ne || nw != sw || nw != se)
                    cornerDistinctCounts[row + cx] = (byte)CountDistinctFaces(nw, ne, sw, se);
            }
        }

        var directedEdges = new List<GridEdge>(w * 16);

        for (var cy = 0; cy < h; cy++)
        {
            for (var cx = 0; cx <= w; cx++)
            {
                var west = SampleCell(cx - 1, cy);
                var east = SampleCell(cx, cy);
                if (west == east)
                    continue;

                var cTop = cy * cw + cx;
                var cBot = (cy + 1) * cw + cx;
                if (west < east)
                    directedEdges.Add(new GridEdge(cBot, cTop, west, east));
                else
                    directedEdges.Add(new GridEdge(cTop, cBot, east, west));
            }
        }

        for (var cy = 0; cy <= h; cy++)
        {
            for (var cx = 0; cx < w; cx++)
            {
                var north = SampleCell(cx, cy - 1);
                var south = SampleCell(cx, cy);
                if (north == south)
                    continue;

                var cLeft = cy * cw + cx;
                var cRight = cy * cw + (cx + 1);
                if (north < south)
                    directedEdges.Add(new GridEdge(cLeft, cRight, north, south));
                else
                    directedEdges.Add(new GridEdge(cRight, cLeft, south, north));
            }
        }

        var cornerEdgeDegree = new int[totalCorners];
        foreach (var e in directedEdges)
        {
            cornerEdgeDegree[e.StartCorner]++;
            cornerEdgeDegree[e.EndCorner]++;
        }

        var isJunction = new bool[totalCorners];
        for (var i = 0; i < totalCorners; i++)
        {
            if (cornerDistinctCounts[i] >= 3 || (cornerEdgeDegree[i] != 2 && cornerEdgeDegree[i] > 0))
                isJunction[i] = true;
        }

        var edgesByPair = new Dictionary<(int L, int R), List<GridEdge>>();
        foreach (var e in directedEdges)
        {
            var key = (e.LeftFace, e.RightFace);
            if (!edgesByPair.TryGetValue(key, out var list))
                edgesByPair[key] = list = new List<GridEdge>();
            list.Add(e);
        }

        var vertices = new List<TopoVertex>();
        var cornerToVertexId = new Dictionary<int, int>();

        int GetOrCreateVertex(int cornerIdx)
        {
            if (cornerToVertexId.TryGetValue(cornerIdx, out var vid))
                return vid;
            var cy = cornerIdx / cw;
            var cx = cornerIdx - cy * cw;
            vid = vertices.Count + 1;
            vertices.Add(new TopoVertex(vid, cx / (float)w, cy / (float)h));
            cornerToVertexId[cornerIdx] = vid;
            return vid;
        }

        var arcs = new List<SharedArcRecord>();
        var nextArcId = 1;

        foreach (var ((leftFace, rightFace), pairEdges) in edgesByPair)
        {
            var outByStart = new Dictionary<int, List<int>>();
            for (var i = 0; i < pairEdges.Count; i++)
            {
                var sc = pairEdges[i].StartCorner;
                if (!outByStart.TryGetValue(sc, out var list))
                    outByStart[sc] = list = new List<int>();
                list.Add(i);
            }

            var used = new bool[pairEdges.Count];

            for (var i = 0; i < pairEdges.Count; i++)
            {
                if (used[i] || !isJunction[pairEdges[i].StartCorner])
                    continue;
                TraceArcChain(i, pairEdges, used, outByStart, isJunction, w, h, cw,
                    locByFaceId, leftFace, rightFace, ref nextArcId, GetOrCreateVertex, arcs);
            }

            for (var i = 0; i < pairEdges.Count; i++)
            {
                if (used[i])
                    continue;
                isJunction[pairEdges[i].StartCorner] = true;
                TraceArcChain(i, pairEdges, used, outByStart, isJunction, w, h, cw,
                    locByFaceId, leftFace, rightFace, ref nextArcId, GetOrCreateVertex, arcs);
            }
        }

        var arcsByFace = new Dictionary<int, List<SharedArcRecord>>();
        var adjPairsSet = new HashSet<(int A, int B)>();
        var coastalFaces = new HashSet<int>();
        var typedEdges = new List<(int A, int B, LocationEdgeInfo Info)>();
        var addedEdgeKeys = new HashSet<(int, int)>();

        bool IsSeaFace(int fid) => locByFaceId.TryGetValue(fid, out var l) && l.Kind == LocationKind.SeaZone;
        bool IsPassableLand(int fid) => locByFaceId.TryGetValue(fid, out var l) && (l.Kind == LocationKind.Habitable || l.Kind == LocationKind.Corridor);

        foreach (var arc in arcs)
        {
            var leftSea = arc.LeftFaceId > 0 && IsSeaFace(arc.LeftFaceId);
            var rightSea = arc.RightFaceId > 0 && IsSeaFace(arc.RightFaceId);

            if (arc.LeftFaceId == 0 && IsPassableLand(arc.RightFaceId))
                coastalFaces.Add(arc.RightFaceId);
            else if (arc.RightFaceId == 0 && IsPassableLand(arc.LeftFaceId))
                coastalFaces.Add(arc.LeftFaceId);
            else if (leftSea && IsPassableLand(arc.RightFaceId))
                coastalFaces.Add(arc.RightFaceId);
            else if (rightSea && IsPassableLand(arc.LeftFaceId))
                coastalFaces.Add(arc.LeftFaceId);

            if (arc.LeftFaceId > 0 && arc.RightFaceId > 0)
            {
                var minF = Math.Min(arc.LeftFaceId, arc.RightFaceId);
                var maxF = Math.Max(arc.LeftFaceId, arc.RightFaceId);
                adjPairsSet.Add((minF, maxF));

                if (addedEdgeKeys.Add((minF, maxF)))
                {
                    var kind = (leftSea && rightSea)
                        ? LocationEdgeKind.SeaSea
                        : (leftSea || rightSea)
                            ? LocationEdgeKind.CoastSea
                            : LocationEdgeKind.LandLand;
                    var hasRiver = kind == LocationEdgeKind.LandLand && ArcIntersectsAnyRiver(arc.Points, features.Rivers);
                    var roadLevel = ResolveSharedRoadLevel(locByFaceId, minF, maxF);
                    typedEdges.Add((minF, maxF, new LocationEdgeInfo(kind, HasRiver: hasRiver, RoadLevel: roadLevel)));
                }
            }

            if (arc.LeftFaceId > 0)
            {
                if (!arcsByFace.TryGetValue(arc.LeftFaceId, out var lList))
                    arcsByFace[arc.LeftFaceId] = lList = new List<SharedArcRecord>();
                lList.Add(arc);
            }

            if (arc.RightFaceId > 0)
            {
                if (!arcsByFace.TryGetValue(arc.RightFaceId, out var rList))
                    arcsByFace[arc.RightFaceId] = rList = new List<SharedArcRecord>();
                rList.Add(arc);
            }
        }

        // Add explicit StraitCrossings symmetrically
        foreach (var strait in features.Straits)
        {
            if (locById.TryGetValue(strait.ProvinceA, out var la) &&
                locById.TryGetValue(strait.ProvinceB, out var lb) &&
                la.PersistentUid != lb.PersistentUid)
            {
                var minF = Math.Min(la.PersistentUid, lb.PersistentUid);
                var maxF = Math.Max(la.PersistentUid, lb.PersistentUid);
                adjPairsSet.Add((minF, maxF));
                if (addedEdgeKeys.Add((minF, maxF)))
                    typedEdges.Add((minF, maxF, new LocationEdgeInfo(LocationEdgeKind.Strait)));
            }
        }

        // Add declared Corridor connections symmetrically if raster corridor fell short of neighbor edge
        if (corridorConnections is not null)
        {
            foreach (var (corrId, targets) in corridorConnections)
            {
                if (!locById.TryGetValue(corrId, out var cloc))
                    continue;
                foreach (var tid in targets)
                {
                    if (!locById.TryGetValue(tid, out var tloc) || cloc.PersistentUid == tloc.PersistentUid)
                        continue;
                    var minF = Math.Min(cloc.PersistentUid, tloc.PersistentUid);
                    var maxF = Math.Max(cloc.PersistentUid, tloc.PersistentUid);
                    adjPairsSet.Add((minF, maxF));
                    if (addedEdgeKeys.Add((minF, maxF)))
                        typedEdges.Add((minF, maxF, new LocationEdgeInfo(LocationEdgeKind.LandLand)));
                }
            }
        }

        var adjPairs = adjPairsSet.OrderBy(p => p.A).ThenBy(p => p.B).ToList();
        var neighborMap = new Dictionary<int, HashSet<string>>();
        var stateAdjSet = new HashSet<(string A, string B)>();

        foreach (var (fa, fb) in adjPairs)
        {
            if (!locByFaceId.TryGetValue(fa, out var la) || !locByFaceId.TryGetValue(fb, out var lb))
                continue;

            if (!neighborMap.TryGetValue(fa, out var setA))
                neighborMap[fa] = setA = new HashSet<string>(StringComparer.Ordinal);
            if (!neighborMap.TryGetValue(fb, out var setB))
                neighborMap[fb] = setB = new HashSet<string>(StringComparer.Ordinal);
            setA.Add(lb.Id);
            setB.Add(la.Id);

            if (la.Kind == LocationKind.Habitable && lb.Kind == LocationKind.Habitable &&
                !string.IsNullOrEmpty(la.AreaId) && !string.IsNullOrEmpty(lb.AreaId) &&
                !string.Equals(la.AreaId, lb.AreaId, StringComparison.Ordinal))
            {
                var sa = string.CompareOrdinal(la.AreaId, lb.AreaId) < 0 ? la.AreaId : lb.AreaId;
                var sb = string.CompareOrdinal(la.AreaId, lb.AreaId) < 0 ? lb.AreaId : la.AreaId;
                stateAdjSet.Add((sa, sb));
            }
        }

        var faces = new List<FaceRecord>(locByFaceId.Count);
        foreach (var loc in locByFaceId.Values.OrderBy(x => x.PersistentUid))
        {
            arcsByFace.TryGetValue(loc.PersistentUid, out var faceArcs);
            faceArcs ??= new List<SharedArcRecord>();

            var outerRing = ReconstructOuterRing(loc.PersistentUid, faceArcs);
            var area = PlanarMapTopology.ComputePolygonArea(outerRing);
            var adjIds = neighborMap.TryGetValue(loc.PersistentUid, out var nSet)
                ? nSet.OrderBy(x => x, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();

            faces.Add(new FaceRecord(
                FaceId: loc.PersistentUid,
                PersistentUid: loc.PersistentUid,
                ProvinceId: loc.Id,
                StateId: loc.AreaId,
                OwnerNationId: loc.OwnerNationId ?? "NONE",
                Terrain: loc.Topography.ToString().ToLowerInvariant(),
                SeedX: loc.SeedX,
                SeedY: loc.SeedY,
                AreaNormalized: area,
                IsCoastal: loc.Kind != LocationKind.SeaZone && loc.Kind != LocationKind.Impassable && coastalFaces.Contains(loc.PersistentUid),
                ArcIds: faceArcs.Select(a => a.Id).ToArray(),
                OuterRing: outerRing,
                AdjacentProvinceIds: adjIds,
                Kind: loc.Kind));
        }

        var stateAdjList = stateAdjSet.OrderBy(x => x.A, StringComparer.Ordinal).ThenBy(x => x.B, StringComparer.Ordinal).ToList();
        return new PlanarMapTopology(vertices, arcs, faces, adjPairs, stateAdjList, null, typedEdges);
    }

    private static RoadLevel ResolveSharedRoadLevel(Dictionary<int, LocationDefinition> locByFaceId, int faceA, int faceB)
    {
        if (!locByFaceId.TryGetValue(faceA, out var la) || !locByFaceId.TryGetValue(faceB, out var lb))
            return RoadLevel.None;
        return la.RoadLevel < lb.RoadLevel ? la.RoadLevel : lb.RoadLevel;
    }

    private static bool ArcIntersectsAnyRiver(Point2D[] arcPoints, RiverFeature[] rivers)
    {
        if (rivers.Length == 0 || arcPoints.Length == 0)
            return false;

        var mid = arcPoints[arcPoints.Length / 2];
        for (var i = 0; i < rivers.Length; i++)
        {
            if (MapWorldGeometrySampler.DistanceToPolyline(mid.X, mid.Y, rivers[i].ControlPoints) <= 0.015f)
                return true;
        }
        return false;
    }

    private static void TraceArcChain(
        int startEdgeIdx,
        List<GridEdge> pairEdges,
        bool[] used,
        Dictionary<int, List<int>> outByStart,
        bool[] isJunction,
        int w,
        int h,
        int cw,
        Dictionary<int, LocationDefinition> locByFaceId,
        int leftFace,
        int rightFace,
        ref int nextArcId,
        Func<int, int> getOrCreateVertex,
        List<SharedArcRecord> arcs)
    {
        var chainCorners = new List<int>();
        var currIdx = startEdgeIdx;
        var startCorner = pairEdges[currIdx].StartCorner;
        chainCorners.Add(startCorner);

        while (true)
        {
            used[currIdx] = true;
            var endCorner = pairEdges[currIdx].EndCorner;
            chainCorners.Add(endCorner);

            if (isJunction[endCorner] || endCorner == startCorner)
                break;

            if (!outByStart.TryGetValue(endCorner, out var candidates))
                break;

            var nextIdx = -1;
            for (var c = 0; c < candidates.Count; c++)
            {
                if (!used[candidates[c]])
                {
                    nextIdx = candidates[c];
                    break;
                }
            }

            if (nextIdx < 0)
                break;
            currIdx = nextIdx;
        }

        var startVid = getOrCreateVertex(chainCorners[0]);
        var endVid = getOrCreateVertex(chainCorners[^1]);

        var rawPts = new Point2D[chainCorners.Count];
        for (var i = 0; i < chainCorners.Count; i++)
        {
            var cy = chainCorners[i] / cw;
            var cx = chainCorners[i] - cy * cw;
            rawPts[i] = new Point2D(cx / (float)w, cy / (float)h);
        }

        var smoothed = SmoothInteriorPoints(rawPts);
        var simplified = SimplifyPolyline(smoothed, 0.0012f);

        var leftSea = leftFace == 0 || (locByFaceId.TryGetValue(leftFace, out var ll) && ll.Kind == LocationKind.SeaZone);
        var rightSea = rightFace == 0 || (locByFaceId.TryGetValue(rightFace, out var rl) && rl.Kind == LocationKind.SeaZone);
        var isCoast = leftSea != rightSea;

        var isIntl = !leftSea && !rightSea &&
                     locByFaceId.TryGetValue(leftFace, out var pl) &&
                     locByFaceId.TryGetValue(rightFace, out var pr) &&
                     !string.Equals(pl.OwnerNationId, pr.OwnerNationId, StringComparison.Ordinal);

        arcs.Add(new SharedArcRecord(
            Id: nextArcId++,
            LeftFaceId: leftFace,
            RightFaceId: rightFace,
            StartVertexId: startVid,
            EndVertexId: endVid,
            Points: simplified,
            IsCoastline: isCoast,
            IsInternationalBorder: isIntl));
    }

    private static Point2D[] SmoothInteriorPoints(Point2D[] pts)
    {
        if (pts.Length <= 2)
            return pts;

        var res = new Point2D[pts.Length];
        res[0] = pts[0];
        res[^1] = pts[^1];
        for (var i = 1; i < pts.Length - 1; i++)
        {
            res[i] = new Point2D(
                0.22f * pts[i - 1].X + 0.56f * pts[i].X + 0.22f * pts[i + 1].X,
                0.22f * pts[i - 1].Y + 0.56f * pts[i].Y + 0.22f * pts[i + 1].Y);
        }
        return res;
    }

    public static Point2D[] SimplifyPolyline(Point2D[] pts, float epsilon)
    {
        if (pts.Length <= 3)
            return pts;

        // Closed loops keep closed ring endpoints intact when treated as LineString
        var coords = new Coordinate[pts.Length];
        for (var i = 0; i < pts.Length; i++)
            coords[i] = new Coordinate(pts[i].X, pts[i].Y);

        var line = GeomFactory.CreateLineString(coords);
        var simplified = DouglasPeuckerSimplifier.Simplify(line, epsilon);
        var outCoords = simplified.Coordinates;
        if (outCoords.Length < 2)
            return pts;

        var result = new Point2D[outCoords.Length];
        for (var i = 0; i < outCoords.Length; i++)
            result[i] = new Point2D((float)outCoords[i].X, (float)outCoords[i].Y);
        result[0] = pts[0];
        result[^1] = pts[^1];
        return result;
    }

    public static Point2D[] ReconstructOuterRing(int faceId, IReadOnlyList<SharedArcRecord> faceArcs)
    {
        if (faceArcs.Count == 0)
            return Array.Empty<Point2D>();

        var segments = new List<(int StartV, int EndV, Point2D[] Pts)>(faceArcs.Count);
        foreach (var arc in faceArcs)
        {
            if (arc.LeftFaceId == faceId)
            {
                segments.Add((arc.StartVertexId, arc.EndVertexId, arc.Points));
            }
            else if (arc.RightFaceId == faceId)
            {
                var rev = new Point2D[arc.Points.Length];
                for (var i = 0; i < rev.Length; i++)
                    rev[i] = arc.Points[arc.Points.Length - 1 - i];
                segments.Add((arc.EndVertexId, arc.StartVertexId, rev));
            }
        }

        if (segments.Count == 0)
            return Array.Empty<Point2D>();

        var used = new bool[segments.Count];
        Point2D[]? bestRing = null;
        var bestArea = -1f;

        for (var startSeg = 0; startSeg < segments.Count; startSeg++)
        {
            if (used[startSeg])
                continue;

            var ringPts = new List<Point2D>(32);
            var cur = startSeg;
            var loopStartVertex = segments[cur].StartV;
            used[cur] = true;

            for (var p = 0; p < segments[cur].Pts.Length - 1; p++)
                ringPts.Add(segments[cur].Pts[p]);

            var currEndVertex = segments[cur].EndV;
            var guard = 0;
            while (currEndVertex != loopStartVertex && guard++ < segments.Count + 2)
            {
                var nextSeg = -1;
                for (var cand = 0; cand < segments.Count; cand++)
                {
                    if (!used[cand] && segments[cand].StartV == currEndVertex)
                    {
                        nextSeg = cand;
                        break;
                    }
                }
                if (nextSeg < 0)
                    break;

                used[nextSeg] = true;
                for (var p = 0; p < segments[nextSeg].Pts.Length - 1; p++)
                    ringPts.Add(segments[nextSeg].Pts[p]);
                currEndVertex = segments[nextSeg].EndV;
            }

            if (ringPts.Count >= 3)
            {
                var arr = ringPts.ToArray();
                var area = PlanarMapTopology.ComputePolygonArea(arr);
                if (area > bestArea)
                {
                    bestArea = area;
                    bestRing = arr;
                }
            }
        }

        return bestRing ?? Array.Empty<Point2D>();
    }
}
