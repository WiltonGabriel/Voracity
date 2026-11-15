using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Core.Mapping;

/// <summary>
/// Builds a watertight planar vector topology (Vertices, SharedArcs, Faces) from province anchors + landmass contours.
/// Guarantees zero vertex duplication across shared borders, 100% symmetrical adjacencies, and derived IsCoastal flags.
/// </summary>
public static class PlanarTopologyBuilder
{
    public const int TopoGridWidth = 540;
    public const int TopoGridHeight = 315;
    private const float AspectX = 1.714f;

    public static PlanarMapTopology BuildFromProvinces(
        IReadOnlyList<ProvinceSnapshot> provinces,
        GeographyFeaturesData? features = null,
        int gridWidth = TopoGridWidth,
        int gridHeight = TopoGridHeight)
    {
        features ??= GeographyFeaturesData.Empty;
        var w = gridWidth;
        var h = gridHeight;
        var pCount = provinces.Count;

        var idGrid = new int[w * h];
        var spatialSeeds = new SpatialGrid2D<int>(48, 28);
        var provByFaceId = new Dictionary<int, ProvinceSnapshot>(pCount);
        var seedInfo = new (float X, float Y, bool IsCoastal)[pCount];

        for (var i = 0; i < pCount; i++)
        {
            var p = provinces[i];
            provByFaceId[p.NumericalIndex] = p;
            seedInfo[i] = (p.AnchorX, p.AnchorY, p.IsCoastal);
            spatialSeeds.Add(p.AnchorX, p.AnchorY, p.NumericalIndex);
        }

        var hasLandmasses = features.Landmasses.Length > 0;

        // Step 1: Assign every grid cell via domain-warped SpatialGrid Voronoi clipped by Landmass polygons
        for (var y = 0; y < h; y++)
        {
            var ny = (y + 0.5f) / h;
            var rowOffset = y * w;

            for (var x = 0; x < w; x++)
            {
                var nx = (x + 0.5f) / w;
                var idx = rowOffset + x;

                if (nx < 0.02f || nx > 0.98f || ny < 0.025f || ny > 0.975f)
                    continue;

                bool isLand;
                if (hasLandmasses)
                {
                    isLand = IsLandInsideContours(nx, ny, features);
                }
                else
                {
                    isLand = EvaluateMetaballLand(nx, ny, seedInfo, spatialSeeds, out _);
                }

                if (!isLand)
                    continue;

                var wx = nx + PontariaMapGenerator.Fbm2D(nx * 11.5f + 3.1f, ny * 8.2f + 1.7f) * 0.018f;
                var wy = ny + PontariaMapGenerator.Fbm2D(nx * 10.8f + 9.4f, ny * 9.1f + 5.3f) * 0.016f;
                var nearestSlot = spatialSeeds.FindNearest(wx, wy, AspectX, 0.35f);
                if (nearestSlot >= 0)
                    idGrid[idx] = spatialSeeds.GetAt(nearestSlot).Item;
            }
        }

        // Guarantee every province owns at least its seed pixel neighborhood so no face disappears
        for (var i = 0; i < pCount; i++)
        {
            var p = provinces[i];
            var gx = Math.Clamp((int)(p.AnchorX * w), 1, w - 2);
            var gy = Math.Clamp((int)(p.AnchorY * h), 1, h - 2);
            var hasPixel = false;
            for (var dy = -2; dy <= 2 && !hasPixel; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    if (idGrid[(gy + dy) * w + (gx + dx)] == p.NumericalIndex)
                    {
                        hasPixel = true;
                        break;
                    }
                }
            }

            if (!hasPixel)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                        idGrid[(gy + dy) * w + (gx + dx)] = p.NumericalIndex;
                }
            }
        }

        return ExtractTopologyFromIdGrid(idGrid, w, h, provByFaceId, features);
    }

    internal static bool IsLandInsideContours(float nx, float ny, GeographyFeaturesData features)
    {
        // Organic micro-coastline perturbation keeps polygon edges natural like antique cartography
        var jitterX = PontariaMapGenerator.Fbm2D(nx * 24f + 4.1f, ny * 18f + 2.7f) * 0.008f;
        var jitterY = PontariaMapGenerator.Fbm2D(nx * 22f + 9.3f, ny * 19f + 6.4f) * 0.007f;
        var sx = nx + jitterX;
        var sy = ny + jitterY;

        if (nx >= 0.923f && ny >= 0.415f && ny <= 0.490f)
            return false;
        if (nx >= 0.938f)
            return false;

        var inContinentOrIsland = false;
        foreach (var lm in features.Landmasses)
        {
            if (lm.Kind == "inland_sea_mask")
            {
                if (PlanarMapTopology.PointInPolygon(lm.PolygonPoints, sx, sy))
                    return false;
            }
            else if (!string.Equals(lm.Kind, "wasteland_continent", StringComparison.Ordinal)
                     && PlanarMapTopology.PointInPolygon(lm.PolygonPoints, sx, sy))
            {
                inContinentOrIsland = true;
            }
        }

        if (!inContinentOrIsland)
            return false;

        foreach (var lake in features.Lakes)
        {
            if (PlanarMapTopology.PointInPolygon(lake.PolygonPoints, nx, ny))
                return false;
        }

        return true;
    }

    private static bool EvaluateMetaballLand(
        float nx,
        float ny,
        (float X, float Y, bool IsCoastal)[] seedInfo,
        SpatialGrid2D<int> spatialSeeds,
        out float potential)
    {
        var wx = nx + PontariaMapGenerator.Fbm2D(nx * 11.5f + 3.1f, ny * 8.2f + 1.7f) * 0.024f;
        var wy = ny + PontariaMapGenerator.Fbm2D(nx * 10.8f + 9.4f, ny * 9.1f + 5.3f) * 0.022f;

        potential = 0f;
        var bestDistSq = float.MaxValue;

        // Check candidates locally if dense; fallback scan if small atlas (<400 provinces)
        if (seedInfo.Length <= 400)
        {
            for (var p = 0; p < seedInfo.Length; p++)
            {
                var dx = (wx - seedInfo[p].X) * AspectX;
                var dy = wy - seedInfo[p].Y;
                var dSq = dx * dx + dy * dy;
                if (dSq < bestDistSq)
                    bestDistSq = dSq;

                var radiusSq = seedInfo[p].IsCoastal ? 0.0145f : 0.0175f;
                var contrib = radiusSq / (dSq + 0.0022f);
                if (contrib > potential)
                    potential = contrib;
                else
                    potential += contrib * 0.28f;
            }
        }
        else
        {
            var local = new List<int>(24);
            spatialSeeds.QueryRect(wx - 0.18f, wy - 0.18f, wx + 0.18f, wy + 0.18f, local);
            for (var i = 0; i < local.Count; i++)
            {
                var faceId = local[i];
                var s = seedInfo[Math.Clamp(faceId - 1, 0, seedInfo.Length - 1)];
                var dx = (wx - s.X) * AspectX;
                var dy = wy - s.Y;
                var dSq = dx * dx + dy * dy;
                if (dSq < bestDistSq)
                    bestDistSq = dSq;
                var radiusSq = s.IsCoastal ? 0.0145f : 0.0175f;
                var contrib = radiusSq / (dSq + 0.0022f);
                if (contrib > potential)
                    potential = contrib;
                else
                    potential += contrib * 0.28f;
            }
        }

        var coastNoise = PontariaMapGenerator.Fbm2D(nx * 19.0f + 14.2f, ny * 14.5f + 8.7f) * 0.34f;
        var gulfs = 0.0f;
        if (nx > 0.21f && nx < 0.39f && ny > 0.68f) gulfs += 0.45f;
        if (nx > 0.47f && nx < 0.66f && ny > 0.85f) gulfs += 0.40f;
        if (ny < 0.065f || ny > 0.91f || nx < 0.045f || nx > 0.94f) gulfs += 0.85f;

        var effective = potential + coastNoise - gulfs;
        return effective >= 1.18f && bestDistSq < 0.032f;
    }

    private readonly record struct GridEdge(int StartCorner, int EndCorner, int LeftFace, int RightFace);

    private static PlanarMapTopology ExtractTopologyFromIdGrid(
        int[] idGrid,
        int w,
        int h,
        Dictionary<int, ProvinceSnapshot> provByFaceId,
        GeographyFeaturesData features)
    {
        var cw = w + 1;

        int SampleCell(int x, int y)
        {
            if (x < 0 || x >= w || y < 0 || y >= h)
                return 0;
            return idGrid[y * w + x];
        }

        // Collect directed boundary unit segments oriented so LeftFace has face on left-hand side of Start->End
        var directedEdges = new List<GridEdge>(w * 16);
        var cornerTouchingFaces = new HashSet<int>[cw * (h + 1)];

        void MarkCorner(int cornerIdx, int f0, int f1, int f2, int f3)
        {
            var set = cornerTouchingFaces[cornerIdx];
            if (set is null)
            {
                set = new HashSet<int>();
                cornerTouchingFaces[cornerIdx] = set;
            }
            set.Add(f0);
            set.Add(f1);
            set.Add(f2);
            set.Add(f3);
        }

        for (var cy = 0; cy <= h; cy++)
        {
            for (var cx = 0; cx <= w; cx++)
            {
                var nw = SampleCell(cx - 1, cy - 1);
                var ne = SampleCell(cx, cy - 1);
                var sw = SampleCell(cx - 1, cy);
                var se = SampleCell(cx, cy);
                if (nw != ne || nw != sw || nw != se)
                    MarkCorner(cy * cw + cx, nw, ne, sw, se);
            }
        }

        // Vertical grid edges: (cx, cy) -> (cx, cy+1); walking downward: Right hand = West (cx-1), Left hand = East (cx)
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
                // Canonical edge orientation: minFace on Left, maxFace on Right
                if (west < east)
                    directedEdges.Add(new GridEdge(cBot, cTop, west, east));
                else
                    directedEdges.Add(new GridEdge(cTop, cBot, east, west));
            }
        }

        // Horizontal grid edges: (cx, cy) -> (cx+1, cy); walking rightward: Left hand = North (cy-1), Right hand = South (cy)
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

        // Corners where >= 3 distinct faces meet (or corner where edge branches) are Junction TopoVertices
        var cornerEdgeDegree = new int[cw * (h + 1)];
        foreach (var e in directedEdges)
        {
            cornerEdgeDegree[e.StartCorner]++;
            cornerEdgeDegree[e.EndCorner]++;
        }

        var isJunction = new bool[cw * (h + 1)];
        for (var i = 0; i < isJunction.Length; i++)
        {
            var fCount = cornerTouchingFaces[i]?.Count ?? 0;
            if (fCount >= 3 || cornerEdgeDegree[i] != 2 && cornerEdgeDegree[i] > 0)
                isJunction[i] = true;
        }

        // Group edges by canonical (LeftFace, RightFace) pair to stitch arcs cleanly
        var edgesByPair = new Dictionary<(int L, int R), List<GridEdge>>();
        foreach (var e in directedEdges)
        {
            var key = (e.LeftFace, e.RightFace);
            if (!edgesByPair.TryGetValue(key, out var list))
            {
                list = new List<GridEdge>();
                edgesByPair[key] = list;
            }
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
                {
                    list = new List<int>();
                    outByStart[sc] = list;
                }
                list.Add(i);
            }

            var used = new bool[pairEdges.Count];

            // Pass 1: Trace chains starting from junction corners
            for (var i = 0; i < pairEdges.Count; i++)
            {
                if (used[i] || !isJunction[pairEdges[i].StartCorner])
                    continue;
                TraceArcChain(i, pairEdges, used, outByStart, isJunction, w, h, cw,
                    provByFaceId, leftFace, rightFace, ref nextArcId, GetOrCreateVertex, arcs);
            }

            // Pass 2: Closed loops (e.g. enclaves or islands with zero triple-junctions)
            for (var i = 0; i < pairEdges.Count; i++)
            {
                if (used[i])
                    continue;
                isJunction[pairEdges[i].StartCorner] = true;
                TraceArcChain(i, pairEdges, used, outByStart, isJunction, w, h, cw,
                    provByFaceId, leftFace, rightFace, ref nextArcId, GetOrCreateVertex, arcs);
            }
        }

        // Build FaceRecords + Adjacencies + Derived IsCoastal
        var arcsByFace = new Dictionary<int, List<SharedArcRecord>>();
        var adjPairsSet = new HashSet<(int A, int B)>();
        var coastalFaces = new HashSet<int>();

        foreach (var arc in arcs)
        {
            if (arc.LeftFaceId == 0 && arc.RightFaceId > 0)
                coastalFaces.Add(arc.RightFaceId);
            else if (arc.RightFaceId == 0 && arc.LeftFaceId > 0)
                coastalFaces.Add(arc.LeftFaceId);
            else if (arc.LeftFaceId > 0 && arc.RightFaceId > 0)
            {
                var minF = Math.Min(arc.LeftFaceId, arc.RightFaceId);
                var maxF = Math.Max(arc.LeftFaceId, arc.RightFaceId);
                adjPairsSet.Add((minF, maxF));
            }

            if (arc.LeftFaceId > 0)
            {
                if (!arcsByFace.TryGetValue(arc.LeftFaceId, out var lList))
                {
                    lList = new List<SharedArcRecord>();
                    arcsByFace[arc.LeftFaceId] = lList;
                }
                lList.Add(arc);
            }

            if (arc.RightFaceId > 0)
            {
                if (!arcsByFace.TryGetValue(arc.RightFaceId, out var rList))
                {
                    rList = new List<SharedArcRecord>();
                    arcsByFace[arc.RightFaceId] = rList;
                }
                rList.Add(arc);
            }
        }

        // Add StraitCrossings symmetrically into province adjacency
        var faceIdByProvId = provByFaceId.Values.ToDictionary(p => p.ProvinceId, p => p.NumericalIndex, StringComparer.Ordinal);
        foreach (var strait in features.Straits)
        {
            if (faceIdByProvId.TryGetValue(strait.ProvinceA, out var fa) &&
                faceIdByProvId.TryGetValue(strait.ProvinceB, out var fb) &&
                fa != fb)
            {
                adjPairsSet.Add((Math.Min(fa, fb), Math.Max(fa, fb)));
            }
        }

        var adjPairs = adjPairsSet.OrderBy(p => p.A).ThenBy(p => p.B).ToList();
        var neighborMap = new Dictionary<int, HashSet<string>>();
        foreach (var (fa, fb) in adjPairs)
        {
            if (provByFaceId.TryGetValue(fa, out var pa) && provByFaceId.TryGetValue(fb, out var pb))
            {
                if (!neighborMap.TryGetValue(fa, out var setA))
                    neighborMap[fa] = setA = new HashSet<string>(StringComparer.Ordinal);
                if (!neighborMap.TryGetValue(fb, out var setB))
                    neighborMap[fb] = setB = new HashSet<string>(StringComparer.Ordinal);
                setA.Add(pb.ProvinceId);
                setB.Add(pa.ProvinceId);
            }
        }

        var faces = new List<FaceRecord>(provByFaceId.Count);
        var stateAdjSet = new HashSet<(string A, string B)>();

        foreach (var (a, b) in adjPairs)
        {
            if (provByFaceId.TryGetValue(a, out var pa) && provByFaceId.TryGetValue(b, out var pb) &&
                pa.StateId != pb.StateId)
            {
                var sa = string.CompareOrdinal(pa.StateId, pb.StateId) < 0 ? pa.StateId : pb.StateId;
                var sb = string.CompareOrdinal(pa.StateId, pb.StateId) < 0 ? pb.StateId : pa.StateId;
                stateAdjSet.Add((sa, sb));
            }
        }

        foreach (var p in provByFaceId.Values.OrderBy(x => x.NumericalIndex))
        {
            arcsByFace.TryGetValue(p.NumericalIndex, out var faceArcs);
            faceArcs ??= new List<SharedArcRecord>();

            var outerRing = ReconstructOuterRing(p.NumericalIndex, faceArcs);
            var area = PlanarMapTopology.ComputePolygonArea(outerRing);
            var adjProvIds = neighborMap.TryGetValue(p.NumericalIndex, out var nSet)
                ? nSet.OrderBy(x => x, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();

            faces.Add(new FaceRecord(
                FaceId: p.NumericalIndex,
                PersistentUid: p.NumericalIndex,
                ProvinceId: p.ProvinceId,
                StateId: p.StateId,
                OwnerNationId: p.OwnerNationId,
                Terrain: p.Terrain,
                SeedX: p.AnchorX,
                SeedY: p.AnchorY,
                AreaNormalized: area,
                IsCoastal: coastalFaces.Contains(p.NumericalIndex),
                ArcIds: faceArcs.Select(a => a.Id).ToArray(),
                OuterRing: outerRing,
                AdjacentProvinceIds: adjProvIds));
        }

        var stateAdjList = stateAdjSet.OrderBy(x => x.A, StringComparer.Ordinal).ThenBy(x => x.B, StringComparer.Ordinal).ToList();
        return new PlanarMapTopology(vertices, arcs, faces, adjPairs, stateAdjList);
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
        Dictionary<int, ProvinceSnapshot> provByFaceId,
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

        var isCoast = leftFace == 0 || rightFace == 0;
        var isIntl = !isCoast &&
                     provByFaceId.TryGetValue(leftFace, out var pl) &&
                     provByFaceId.TryGetValue(rightFace, out var pr) &&
                     pl.OwnerNationId != pr.OwnerNationId;

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

        var keep = new bool[pts.Length];
        keep[0] = true;
        keep[^1] = true;
        DouglasPeucker(pts, 0, pts.Length - 1, epsilon, keep);

        var count = 0;
        for (var i = 0; i < keep.Length; i++)
            if (keep[i]) count++;

        var result = new Point2D[count];
        var w = 0;
        for (var i = 0; i < pts.Length; i++)
        {
            if (keep[i])
                result[w++] = pts[i];
        }
        return result;
    }

    private static void DouglasPeucker(Point2D[] pts, int start, int end, float eps, bool[] keep)
    {
        if (end <= start + 1)
            return;

        var ax = pts[start].X;
        var ay = pts[start].Y;
        var bx = pts[end].X;
        var by = pts[end].Y;
        var dx = bx - ax;
        var dy = by - ay;
        var len = MathF.Sqrt(dx * dx + dy * dy);

        var maxDist = -1f;
        var maxIdx = -1;

        for (var i = start + 1; i < end; i++)
        {
            float d;
            if (len < 1e-7f)
            {
                var px = pts[i].X - ax;
                var py = pts[i].Y - ay;
                d = MathF.Sqrt(px * px + py * py);
            }
            else
            {
                d = MathF.Abs(dy * pts[i].X - dx * pts[i].Y + bx * ay - by * ax) / len;
            }
            if (d > maxDist)
            {
                maxDist = d;
                maxIdx = i;
            }
        }

        if (maxDist > eps && maxIdx > start)
        {
            keep[maxIdx] = true;
            DouglasPeucker(pts, start, maxIdx, eps, keep);
            DouglasPeucker(pts, maxIdx, end, eps, keep);
        }
    }

    public static Point2D[] ReconstructOuterRing(int faceId, IReadOnlyList<SharedArcRecord> faceArcs)
    {
        if (faceArcs.Count == 0)
            return Array.Empty<Point2D>();

        // Orient every arc so 'faceId' sits on its consistent winding side and chain endVertex -> startVertex
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

        // Pick largest loop if holes/islands exist so OuterRing represents the primary territory polygon
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
