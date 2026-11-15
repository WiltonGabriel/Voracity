using Voracity.Core.Models;

namespace Voracity.Core.Mapping;

public interface IAtlasCommand
{
    string Description { get; }
    void Execute(EditableWorldAtlas atlas);
    void Undo(EditableWorldAtlas atlas);
}

public sealed class AtlasCommandStack
{
    private readonly List<IAtlasCommand> _undoStack = new();
    private readonly List<IAtlasCommand> _redoStack = new();

    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;
    public IReadOnlyList<string> HistoryDescriptions => _undoStack.Select(c => c.Description).ToArray();

    public void Execute(EditableWorldAtlas atlas, IAtlasCommand command)
    {
        command.Execute(atlas);
        _undoStack.Add(command);
        _redoStack.Clear();
    }

    public bool Undo(EditableWorldAtlas atlas)
    {
        if (_undoStack.Count == 0)
            return false;
        var cmd = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        cmd.Undo(atlas);
        _redoStack.Add(cmd);
        return true;
    }

    public bool Redo(EditableWorldAtlas atlas)
    {
        if (_redoStack.Count == 0)
            return false;
        var cmd = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        cmd.Execute(atlas);
        _undoStack.Add(cmd);
        return true;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}

/// <summary>
/// Drags a shared junction TopoVertex or interior SharedArcRecord polyline vertex,
/// updating both touching faces identically without creating gaps or overlapping triangles.
/// </summary>
public sealed class MoveSharedArcVertexCommand : IAtlasCommand
{
    private readonly int _arcId;
    private readonly int _pointIndex;
    private readonly Point2D _newPosition;
    private Point2D _oldPosition;
    private int _snappedVertexId = -1;
    private readonly List<(int ArcId, int PtIdx, Point2D OldPt)> _syncedArcEndpoints = new();

    public MoveSharedArcVertexCommand(int arcId, int pointIndex, Point2D newPosition)
    {
        _arcId = arcId;
        _pointIndex = pointIndex;
        _newPosition = new Point2D(Math.Clamp(newPosition.X, 0.005f, 0.995f), Math.Clamp(newPosition.Y, 0.005f, 0.995f));
    }

    public string Description => $"Move Border Vertex (Arc #{_arcId})";

    public void Execute(EditableWorldAtlas atlas)
    {
        _syncedArcEndpoints.Clear();
        var arcIdx = atlas.Topology.Arcs.FindIndex(a => a.Id == _arcId);
        if (arcIdx < 0)
            return;

        var arc = atlas.Topology.Arcs[arcIdx];
        if (_pointIndex < 0 || _pointIndex >= arc.Points.Length)
            return;

        _oldPosition = arc.Points[_pointIndex];

        // Endpoint vertices are shared TopoVertices across all incident arcs -> snap all connected arcs simultaneously!
        if (_pointIndex == 0 || _pointIndex == arc.Points.Length - 1)
        {
            _snappedVertexId = _pointIndex == 0 ? arc.StartVertexId : arc.EndVertexId;
            var vIdx = atlas.Topology.Vertices.FindIndex(v => v.Id == _snappedVertexId);
            if (vIdx >= 0)
                atlas.Topology.Vertices[vIdx] = new TopoVertex(_snappedVertexId, _newPosition.X, _newPosition.Y);

            for (var i = 0; i < atlas.Topology.Arcs.Count; i++)
            {
                var a = atlas.Topology.Arcs[i];
                if (a.StartVertexId == _snappedVertexId)
                {
                    _syncedArcEndpoints.Add((a.Id, 0, a.Points[0]));
                    var pts = (Point2D[])a.Points.Clone();
                    pts[0] = _newPosition;
                    atlas.Topology.Arcs[i] = a with { Points = pts };
                }
                else if (a.EndVertexId == _snappedVertexId)
                {
                    var last = a.Points.Length - 1;
                    _syncedArcEndpoints.Add((a.Id, last, a.Points[last]));
                    var pts = (Point2D[])a.Points.Clone();
                    pts[last] = _newPosition;
                    atlas.Topology.Arcs[i] = a with { Points = pts };
                }
            }
        }
        else
        {
            var pts = (Point2D[])arc.Points.Clone();
            pts[_pointIndex] = _newPosition;
            atlas.Topology.Arcs[arcIdx] = arc with { Points = pts };
        }

        RebuildAffectedFaces(atlas, arc.LeftFaceId, arc.RightFaceId);
    }

    public void Undo(EditableWorldAtlas atlas)
    {
        if (_snappedVertexId >= 0)
        {
            var vIdx = atlas.Topology.Vertices.FindIndex(v => v.Id == _snappedVertexId);
            if (vIdx >= 0)
                atlas.Topology.Vertices[vIdx] = new TopoVertex(_snappedVertexId, _oldPosition.X, _oldPosition.Y);

            foreach (var (aId, ptIdx, oldPt) in _syncedArcEndpoints)
            {
                var aIdx = atlas.Topology.Arcs.FindIndex(a => a.Id == aId);
                if (aIdx >= 0)
                {
                    var a = atlas.Topology.Arcs[aIdx];
                    var pts = (Point2D[])a.Points.Clone();
                    pts[ptIdx] = oldPt;
                    atlas.Topology.Arcs[aIdx] = a with { Points = pts };
                }
            }
        }
        else
        {
            var arcIdx = atlas.Topology.Arcs.FindIndex(a => a.Id == _arcId);
            if (arcIdx >= 0)
            {
                var arc = atlas.Topology.Arcs[arcIdx];
                var pts = (Point2D[])arc.Points.Clone();
                pts[_pointIndex] = _oldPosition;
                atlas.Topology.Arcs[arcIdx] = arc with { Points = pts };
            }
        }

        var targetArc = atlas.Topology.Arcs.FirstOrDefault(a => a.Id == _arcId);
        if (targetArc is not null)
            RebuildAffectedFaces(atlas, targetArc.LeftFaceId, targetArc.RightFaceId);
    }

    private static void RebuildAffectedFaces(EditableWorldAtlas atlas, int faceA, int faceB)
    {
        for (var i = 0; i < atlas.Topology.Faces.Count; i++)
        {
            var f = atlas.Topology.Faces[i];
            if (f.FaceId != faceA && f.FaceId != faceB && _affectedByAny(atlas, f.FaceId))
                continue;
            var arcs = atlas.Topology.Arcs.Where(a => a.LeftFaceId == f.FaceId || a.RightFaceId == f.FaceId).ToList();
            var ring = PlanarTopologyBuilder.ReconstructOuterRing(f.FaceId, arcs);
            if (ring.Length >= 3)
            {
                atlas.Topology.Faces[i] = f with
                {
                    OuterRing = ring,
                    AreaNormalized = PlanarMapTopology.ComputePolygonArea(ring)
                };
            }
        }
        atlas.Topology.RebuildIndices();
    }

    private static bool _affectedByAny(EditableWorldAtlas _, int __) => false;
}

/// <summary>
/// Splits a province along a line (cutA -> cutB), producing two geometrically clean neighbor polygons
/// and allocating a fresh PersistentUid for the child province while preserving the parent's ID.
/// </summary>
public sealed class SplitProvinceCommand : IAtlasCommand
{
    private readonly string _provinceId;
    private readonly string _newProvinceId;
    private readonly string _newProvinceName;
    private readonly Point2D _cutA;
    private readonly Point2D _cutB;
    private PlanarMapTopology? _prevTopology;
    private StateDefinition? _prevState;
    private int _prevNextUid;

    public SplitProvinceCommand(string provinceId, Point2D cutA, Point2D cutB, string newProvinceId, string newProvinceName)
    {
        _provinceId = provinceId;
        _cutA = cutA;
        _cutB = cutB;
        _newProvinceId = newProvinceId;
        _newProvinceName = newProvinceName;
    }

    public string Description => $"Split Province {_provinceId} -> {_newProvinceId}";

    public void Execute(EditableWorldAtlas atlas)
    {
        var stIdx = atlas.States.FindIndex(s => s.EnsureProvinceDetails().Any(p => p.Id == _provinceId));
        if (stIdx < 0)
            return;

        _prevState = atlas.States[stIdx];
        _prevTopology = CloneTopology(atlas.Topology);
        _prevNextUid = atlas.NextPersistentUid;

        var st = atlas.States[stIdx];
        var details = st.EnsureProvinceDetails();
        var origDef = details.First(p => p.Id == _provinceId);

        Point2D[] ringA = Array.Empty<Point2D>();
        Point2D[] ringB = Array.Empty<Point2D>();
        if (atlas.Topology.FaceByProvinceId.TryGetValue(_provinceId, out var face) && face.OuterRing.Length >= 3)
        {
            ringA = ClipPolygonHalfPlane(face.OuterRing, _cutA, _cutB, keepLeft: true);
            ringB = ClipPolygonHalfPlane(face.OuterRing, _cutA, _cutB, keepLeft: false);
        }

        var centroidA = ringA.Length >= 3
            ? ComputeCentroid(ringA)
            : new Point2D((float)origDef.AnchorX - 0.012f, (float)origDef.AnchorY);
        var centroidB = ringB.Length >= 3
            ? ComputeCentroid(ringB)
            : new Point2D((float)origDef.AnchorX + 0.012f, (float)origDef.AnchorY);

        var newUid = atlas.AllocatePersistentUid(_newProvinceId);
        var newDef = new ProvinceDefinition(
            Id: _newProvinceId,
            Name: _newProvinceName,
            Terrain: origDef.Terrain,
            HubType: origDef.HubType == "city" ? "farm" : origDef.HubType,
            IsCapital: false,
            IsCoastal: origDef.IsCoastal,
            AnchorX: Math.Clamp(centroidB.X, 0.02, 0.98),
            AnchorY: Math.Clamp(centroidB.Y, 0.02, 0.98),
            StartingFortLevel: 0,
            ResourcePotential: origDef.ResourcePotential,
            PersistentUid: newUid);

        var updatedList = new List<ProvinceDefinition>(details.Length + 1);
        foreach (var d in details)
        {
            if (d.Id == _provinceId)
                updatedList.Add(d with { AnchorX = Math.Clamp(centroidA.X, 0.02, 0.98), AnchorY = Math.Clamp(centroidA.Y, 0.02, 0.98) });
            else
                updatedList.Add(d);
        }
        updatedList.Add(newDef);

        atlas.States[stIdx] = st with
        {
            Provinces = updatedList.Select(p => p.Name).ToArray(),
            ProvinceDetails = updatedList.ToArray()
        };

        atlas.RebuildTopologyFromAnchors();
    }

    public void Undo(EditableWorldAtlas atlas)
    {
        if (_prevState is not null)
        {
            var idx = atlas.States.FindIndex(s => s.Id == _prevState.Id);
            if (idx >= 0)
                atlas.States[idx] = _prevState;
        }
        atlas.PersistentUidByProvinceId.Remove(_newProvinceId);
        atlas.NextPersistentUid = _prevNextUid;
        if (_prevTopology is not null)
            atlas.Topology = _prevTopology;
    }

    private static Point2D ComputeCentroid(Point2D[] pts)
    {
        double sx = 0, sy = 0;
        for (var i = 0; i < pts.Length; i++)
        {
            sx += pts[i].X;
            sy += pts[i].Y;
        }
        return new Point2D((float)(sx / pts.Length), (float)(sy / pts.Length));
    }

    public static Point2D[] ClipPolygonHalfPlane(Point2D[] poly, Point2D a, Point2D b, bool keepLeft)
    {
        if (poly.Length < 3)
            return Array.Empty<Point2D>();

        bool IsInside(Point2D p)
        {
            var cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            return keepLeft ? cross >= 0f : cross <= 0f;
        }

        Point2D Intersect(Point2D p1, Point2D p2)
        {
            var dx1 = p2.X - p1.X;
            var dy1 = p2.Y - p1.Y;
            var dx2 = b.X - a.X;
            var dy2 = b.Y - a.Y;
            var denom = dx1 * dy2 - dy1 * dx2;
            if (MathF.Abs(denom) < 1e-8f)
                return p1;
            var t = ((a.X - p1.X) * dy2 - (a.Y - p1.Y) * dx2) / denom;
            return new Point2D(p1.X + t * dx1, p1.Y + t * dy1);
        }

        var output = new List<Point2D>(poly.Length + 2);
        var prev = poly[^1];
        var prevIn = IsInside(prev);
        for (var i = 0; i < poly.Length; i++)
        {
            var curr = poly[i];
            var currIn = IsInside(curr);
            if (currIn)
            {
                if (!prevIn) output.Add(Intersect(prev, curr));
                output.Add(curr);
            }
            else if (prevIn)
            {
                output.Add(Intersect(prev, curr));
            }
            prev = curr;
            prevIn = currIn;
        }
        return output.ToArray();
    }

    internal static PlanarMapTopology CloneTopology(PlanarMapTopology src)
        => new(
            src.Vertices.ToList(),
            src.Arcs.ToList(),
            src.Faces.ToList(),
            src.SymmetricProvinceAdjacencies.ToList(),
            src.SymmetricStateAdjacencies.ToList(),
            src.Tombstones.ToList());
}

/// <summary>
/// Merges an absorbed province into a surviving province and preserves a ProvinceTombstone linking retired UID -> survivor.
/// </summary>
public sealed class MergeProvincesCommand : IAtlasCommand
{
    private readonly string _survivingProvinceId;
    private readonly string _absorbedProvinceId;
    private readonly List<StateDefinition> _prevStates = new();
    private PlanarMapTopology? _prevTopology;
    private ProvinceTombstone? _addedTombstone;

    public MergeProvincesCommand(string survivingProvinceId, string absorbedProvinceId)
    {
        _survivingProvinceId = survivingProvinceId;
        _absorbedProvinceId = absorbedProvinceId;
    }

    public string Description => $"Merge {_absorbedProvinceId} into {_survivingProvinceId}";

    public void Execute(EditableWorldAtlas atlas)
    {
        if (_survivingProvinceId == _absorbedProvinceId)
            return;

        _prevStates.Clear();
        _prevStates.AddRange(atlas.States);
        _prevTopology = SplitProvinceCommand.CloneTopology(atlas.Topology);

        var absorbedUid = atlas.AllocatePersistentUid(_absorbedProvinceId);
        for (var s = 0; s < atlas.States.Count; s++)
        {
            var st = atlas.States[s];
            var details = st.EnsureProvinceDetails();
            if (details.Any(p => p.Id == _absorbedProvinceId))
            {
                var remaining = details.Where(p => p.Id != _absorbedProvinceId).ToArray();
                atlas.States[s] = st with
                {
                    Provinces = remaining.Select(p => p.Name).ToArray(),
                    ProvinceDetails = remaining
                };
            }
        }

        _addedTombstone = new ProvinceTombstone(
            absorbedUid,
            _absorbedProvinceId,
            _survivingProvinceId,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        atlas.RebuildTopologyFromAnchors();
        atlas.Topology.Tombstones.Add(_addedTombstone);
    }

    public void Undo(EditableWorldAtlas atlas)
    {
        atlas.States.Clear();
        atlas.States.AddRange(_prevStates);
        if (_prevTopology is not null)
            atlas.Topology = _prevTopology;
    }
}

/// <summary>
/// Creates a brand new province inside an existing state/area, or removes a non-last province safely.
/// </summary>
public sealed class CreateProvinceCommand : IAtlasCommand
{
    private readonly string _targetStateId;
    private readonly ProvinceDefinition _newProvince;
    private StateDefinition? _prevState;
    private PlanarMapTopology? _prevTopology;

    public CreateProvinceCommand(string targetStateId, ProvinceDefinition newProvince)
    {
        _targetStateId = targetStateId;
        _newProvince = newProvince;
    }

    public string Description => $"Create Province {_newProvince.Name} ({_newProvince.Id})";

    public void Execute(EditableWorldAtlas atlas)
    {
        var idx = atlas.States.FindIndex(s => s.Id == _targetStateId);
        if (idx < 0)
            return;

        _prevState = atlas.States[idx];
        _prevTopology = SplitProvinceCommand.CloneTopology(atlas.Topology);
        var uid = atlas.AllocatePersistentUid(_newProvince.Id);
        var list = atlas.States[idx].EnsureProvinceDetails().ToList();
        list.Add(_newProvince with { PersistentUid = uid });

        atlas.States[idx] = atlas.States[idx] with
        {
            Provinces = list.Select(p => p.Name).ToArray(),
            ProvinceDetails = list.ToArray()
        };
        atlas.RebuildTopologyFromAnchors();
    }

    public void Undo(EditableWorldAtlas atlas)
    {
        if (_prevState is not null)
        {
            var idx = atlas.States.FindIndex(s => s.Id == _prevState.Id);
            if (idx >= 0)
                atlas.States[idx] = _prevState;
        }
        atlas.PersistentUidByProvinceId.Remove(_newProvince.Id);
        if (_prevTopology is not null)
            atlas.Topology = _prevTopology;
    }
}

/// <summary>
/// Transfers ownership of an entire State/Area OR transfers a single province between two States/Nations
/// without touching simulation world state.
/// </summary>
public sealed class TransferTerritoryCommand : IAtlasCommand
{
    private readonly string _provinceOrStateId;
    private readonly string _newOwnerNationId;
    private readonly List<StateDefinition> _prevStates = new();
    private PlanarMapTopology? _prevTopology;

    public TransferTerritoryCommand(string provinceOrStateId, string newOwnerNationId)
    {
        _provinceOrStateId = provinceOrStateId;
        _newOwnerNationId = newOwnerNationId;
    }

    public string Description => $"Transfer {_provinceOrStateId} -> {_newOwnerNationId}";

    public void Execute(EditableWorldAtlas atlas)
    {
        _prevStates.Clear();
        _prevStates.AddRange(atlas.States);
        _prevTopology = SplitProvinceCommand.CloneTopology(atlas.Topology);

        // First check if target is a State/Area ID -> transfer entire state owner
        var stateIdx = atlas.States.FindIndex(s => s.Id == _provinceOrStateId);
        if (stateIdx >= 0)
        {
            atlas.States[stateIdx] = atlas.States[stateIdx] with { OwnerNationId = _newOwnerNationId };
            for (var i = 0; i < atlas.Topology.Faces.Count; i++)
            {
                if (atlas.Topology.Faces[i].StateId == _provinceOrStateId)
                    atlas.Topology.Faces[i] = atlas.Topology.Faces[i] with { OwnerNationId = _newOwnerNationId };
            }
            UpdateBorderInternationalFlags(atlas);
            return;
        }

        // Otherwise move single province into nearest destination state owned by _newOwnerNationId (or create split Polity state)
        var srcStateIdx = atlas.States.FindIndex(s => s.EnsureProvinceDetails().Any(p => p.Id == _provinceOrStateId));
        if (srcStateIdx < 0)
            return;

        var srcState = atlas.States[srcStateIdx];
        if (srcState.OwnerNationId == _newOwnerNationId)
            return;

        var srcDetails = srcState.EnsureProvinceDetails();
        if (srcDetails.Length <= 1)
        {
            atlas.States[srcStateIdx] = srcState with { OwnerNationId = _newOwnerNationId };
        }
        else
        {
            var movedProv = srcDetails.First(p => p.Id == _provinceOrStateId);
            var remaining = srcDetails.Where(p => p.Id != _provinceOrStateId).ToArray();
            atlas.States[srcStateIdx] = srcState with
            {
                Provinces = remaining.Select(p => p.Name).ToArray(),
                ProvinceDetails = remaining
            };

            var dstIdx = atlas.States.FindIndex(s => s.OwnerNationId == _newOwnerNationId);
            if (dstIdx >= 0)
            {
                var dst = atlas.States[dstIdx];
                var dstList = dst.EnsureProvinceDetails().ToList();
                dstList.Add(movedProv with { IsCapital = false });
                atlas.States[dstIdx] = dst with
                {
                    Provinces = dstList.Select(p => p.Name).ToArray(),
                    ProvinceDetails = dstList.ToArray()
                };
            }
        }

        atlas.RebuildTopologyFromAnchors();
    }

    public void Undo(EditableWorldAtlas atlas)
    {
        atlas.States.Clear();
        atlas.States.AddRange(_prevStates);
        if (_prevTopology is not null)
            atlas.Topology = _prevTopology;
    }

    private static void UpdateBorderInternationalFlags(EditableWorldAtlas atlas)
    {
        atlas.Topology.RebuildIndices();
        for (var i = 0; i < atlas.Topology.Arcs.Count; i++)
        {
            var a = atlas.Topology.Arcs[i];
            if (a.LeftFaceId <= 0 || a.RightFaceId <= 0)
                continue;
            var lOwner = atlas.Topology.FaceById.TryGetValue(a.LeftFaceId, out var fl) ? fl.OwnerNationId : "";
            var rOwner = atlas.Topology.FaceById.TryGetValue(a.RightFaceId, out var fr) ? fr.OwnerNationId : "";
            atlas.Topology.Arcs[i] = a with { IsInternationalBorder = lOwner != rOwner };
        }
    }
}

public sealed class RenameTerritoryCommand : IAtlasCommand
{
    public enum TargetKind { Nation, State, Province, SuperRegion, Region }

    private readonly TargetKind _kind;
    private readonly string _targetId;
    private readonly string _newName;
    private string? _oldName;

    public RenameTerritoryCommand(TargetKind kind, string targetId, string newName)
    {
        _kind = kind;
        _targetId = targetId;
        _newName = newName;
    }

    public string Description => $"Rename {_kind} {_targetId} -> {_newName}";

    public void Execute(EditableWorldAtlas atlas) => Apply(atlas, _newName);
    public void Undo(EditableWorldAtlas atlas) { if (_oldName is not null) Apply(atlas, _oldName); }

    private void Apply(EditableWorldAtlas atlas, string nameValue)
    {
        switch (_kind)
        {
            case TargetKind.Nation:
                var nIdx = atlas.Nations.FindIndex(n => n.Id == _targetId);
                if (nIdx >= 0)
                {
                    _oldName ??= atlas.Nations[nIdx].Name;
                    atlas.Nations[nIdx] = atlas.Nations[nIdx] with { Name = nameValue };
                }
                break;
            case TargetKind.State:
                var sIdx = atlas.States.FindIndex(s => s.Id == _targetId);
                if (sIdx >= 0)
                {
                    _oldName ??= atlas.States[sIdx].Name;
                    atlas.States[sIdx] = atlas.States[sIdx] with { Name = nameValue };
                }
                break;
            case TargetKind.Province:
                for (var s = 0; s < atlas.States.Count; s++)
                {
                    var det = atlas.States[s].EnsureProvinceDetails();
                    for (var p = 0; p < det.Length; p++)
                    {
                        if (det[p].Id == _targetId)
                        {
                            _oldName ??= det[p].Name;
                            var copy = (ProvinceDefinition[])det.Clone();
                            copy[p] = copy[p] with { Name = nameValue };
                            atlas.States[s] = atlas.States[s] with
                            {
                                Provinces = copy.Select(x => x.Name).ToArray(),
                                ProvinceDetails = copy
                            };
                            return;
                        }
                    }
                }
                break;
            case TargetKind.SuperRegion:
                var srs = (SuperRegionDefinition[])atlas.Hierarchy.SuperRegions.Clone();
                for (var i = 0; i < srs.Length; i++)
                {
                    if (srs[i].Id == _targetId)
                    {
                        _oldName ??= srs[i].DefaultName;
                        srs[i] = srs[i] with { DefaultName = nameValue };
                        atlas.Hierarchy = atlas.Hierarchy with { SuperRegions = srs };
                        return;
                    }
                }
                break;
            case TargetKind.Region:
                var regs = (RegionDefinition[])atlas.Hierarchy.Regions.Clone();
                for (var i = 0; i < regs.Length; i++)
                {
                    if (regs[i].Id == _targetId)
                    {
                        _oldName ??= regs[i].DefaultName;
                        regs[i] = regs[i] with { DefaultName = nameValue };
                        atlas.Hierarchy = atlas.Hierarchy with { Regions = regs };
                        return;
                    }
                }
                break;
        }
    }
}

public sealed class SetCapitalProvinceCommand : IAtlasCommand
{
    private readonly string _provinceId;
    private string? _prevNationCapitalStateId;
    private string? _ownerNationId;
    private readonly List<StateDefinition> _prevStates = new();

    public SetCapitalProvinceCommand(string provinceId) => _provinceId = provinceId;
    public string Description => $"Set Capital -> {_provinceId}";

    public void Execute(EditableWorldAtlas atlas)
    {
        _prevStates.Clear();
        _prevStates.AddRange(atlas.States);

        for (var s = 0; s < atlas.States.Count; s++)
        {
            var st = atlas.States[s];
            var det = st.EnsureProvinceDetails();
            if (!det.Any(p => p.Id == _provinceId))
                continue;

            _ownerNationId = st.OwnerNationId;
            var nIdx = atlas.Nations.FindIndex(n => n.Id == _ownerNationId);
            if (nIdx >= 0)
            {
                _prevNationCapitalStateId = atlas.Nations[nIdx].CapitalStateId;
                atlas.Nations[nIdx] = atlas.Nations[nIdx] with { CapitalStateId = st.Id };
            }

            // Clear old capitals in owner's states and set new one
            for (var k = 0; k < atlas.States.Count; k++)
            {
                if (atlas.States[k].OwnerNationId != _ownerNationId)
                    continue;
                var d = atlas.States[k].EnsureProvinceDetails();
                var upd = new ProvinceDefinition[d.Length];
                for (var p = 0; p < d.Length; p++)
                    upd[p] = d[p] with { IsCapital = d[p].Id == _provinceId };
                atlas.States[k] = atlas.States[k] with { ProvinceDetails = upd };
            }
            return;
        }
    }

    public void Undo(EditableWorldAtlas atlas)
    {
        atlas.States.Clear();
        atlas.States.AddRange(_prevStates);
        if (_ownerNationId is not null && _prevNationCapitalStateId is not null)
        {
            var nIdx = atlas.Nations.FindIndex(n => n.Id == _ownerNationId);
            if (nIdx >= 0)
                atlas.Nations[nIdx] = atlas.Nations[nIdx] with { CapitalStateId = _prevNationCapitalStateId };
        }
    }
}

public sealed class EditGeographyFeatureCommand : IAtlasCommand
{
    private readonly Func<GeographyFeaturesData, GeographyFeaturesData> _transform;
    private GeographyFeaturesData? _previous;

    public EditGeographyFeatureCommand(string description, Func<GeographyFeaturesData, GeographyFeaturesData> transform)
    {
        Description = description;
        _transform = transform;
    }

    public string Description { get; }

    public void Execute(EditableWorldAtlas atlas)
    {
        _previous = atlas.Features;
        atlas.Features = _transform(atlas.Features);
    }

    public void Undo(EditableWorldAtlas atlas)
    {
        if (_previous is not null)
            atlas.Features = _previous;
    }
}

public sealed class CreateOrRemoveNationCommand : IAtlasCommand
{
    private readonly NationDefinition _nation;
    private readonly bool _isRemoval;

    public CreateOrRemoveNationCommand(NationDefinition nation, bool isRemoval = false)
    {
        _nation = nation;
        _isRemoval = isRemoval;
    }

    public string Description => _isRemoval ? $"Remove Nation {_nation.Id}" : $"Create Nation {_nation.Id}";

    public void Execute(EditableWorldAtlas atlas)
    {
        if (_isRemoval)
            atlas.Nations.RemoveAll(n => n.Id == _nation.Id);
        else if (atlas.Nations.All(n => n.Id != _nation.Id))
            atlas.Nations.Add(_nation);
    }

    public void Undo(EditableWorldAtlas atlas)
    {
        if (_isRemoval)
        {
            if (atlas.Nations.All(n => n.Id != _nation.Id))
                atlas.Nations.Add(_nation);
        }
        else
        {
            atlas.Nations.RemoveAll(n => n.Id == _nation.Id);
        }
    }
}

public sealed class AddEditorPinCommand : IAtlasCommand
{
    private readonly EditorPinMarker _pin;
    public AddEditorPinCommand(EditorPinMarker pin) => _pin = pin;
    public string Description => $"Pin Note ({_pin.Kind}): {_pin.Note}";
    public void Execute(EditableWorldAtlas atlas) => atlas.Pins.Add(_pin);
    public void Undo(EditableWorldAtlas atlas) => atlas.Pins.RemoveAll(p => p.Id == _pin.Id);
}
