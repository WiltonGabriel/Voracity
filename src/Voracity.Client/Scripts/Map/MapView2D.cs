using Godot;
using Voracity.Core.Engine;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Client.Map;

public sealed class MapLayerFlags
{
    public bool ShowTerrainRelief { get; set; } = true;
    public bool ShowBorders { get; set; } = true;
    public bool ShowProvinceBorders { get; set; } = true;
    public bool ShowRivers { get; set; } = true;
    public bool ShowRoads { get; set; } = true;
    public bool ShowHoldings { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool ShowMountainsOverlay { get; set; } = true;
}

/// <summary>
/// Hybrid Cartographic Renderer for Godot 4 (Vulkan + C# .NET):
/// - Authoritative vector PlanarMapTopology -> derived RG8 16-bit Province ID grid + Chamfer Coast UDF
/// - 256x768 RGBA8 ProvinceGpuLookup supporting 65,535 provinces/states/nations without byte overflow
/// - Single-draw-call batched border ArrayMesh reading dynamic ownership from ProvinceGpuLookup
/// - SpatialGrid2D camera culling & pre-baked polylines with zero GC allocations per _Draw()
/// </summary>
public partial class MapView2D : Node2D
{
    public static readonly Vector2 WorldCanvasSize = new(1920, 1120);
    public const float MinCameraZoom = 0.38f;
    public const float MaxCameraZoom = 4.80f;

    private const int LookupWidth = 256;
    private const int LookupHeight = 768; // 256 banks x 3 rows = 65,536 capacity

    private ColorRect _edgeFogRect = null!;
    private ShaderMaterial _edgeFogMaterial = null!;
    private TextureRect _mapRect = null!;
    private ShaderMaterial _mapMaterial = null!;
    private MeshInstance2D _borderMeshNode = null!;
    private ShaderMaterial _borderMaterial = null!;
    private MapOverlayCanvas _overlay = null!;

    private BakedMapArtifact? _baked;
    private PlanarMapTopology? _topology;
    private GeographyFeaturesData _features = GeographyFeaturesData.Empty;
    private string? _vmapCacheDir;

    private ImageTexture? _provinceDataTex;
    private ImageTexture? _heightCoastTex;
    private Image? _lookupImage;
    private ImageTexture? _lookupTex;

    private RenderSnapshot? _latestSnapshot;
    private readonly Dictionary<int, ProvinceSnapshot> _provByNumIdx = new();
    private readonly Dictionary<string, StateSnapshot> _stateById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nationTagById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _stateTagById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shortNationNameCache = new(StringComparer.Ordinal);
    private readonly SpatialGrid2D<ProvinceSnapshot> _provSpatialGrid = new(32, 20);
    private readonly HashSet<int> _dirtyLookupProvinces = new();
    private bool _fullLookupDirty = true;

    // Pre-allocated overlay caches to guarantee ZERO GC allocations inside _Draw()
    private readonly List<Vector2[]> _cachedRiverPolylines = new();
    private readonly List<Vector2[]> _cachedMountainPolylines = new();
    private readonly List<(Vector2[] Pts, int StateIdxA, int StateIdxB, string StateIdA, string StateIdB)> _cachedRoadArcs = new();
    private readonly List<ProvinceSnapshot> _visibleProvScratch = new(256);

    private MapLensMode _currentLens = MapLensMode.Political;
    private float _cameraZoom = 1.0f;
    private float _elapsedSeconds;
    private int _hoveredProvNum;
    private int _selectedProvNum;
    private int _selectedStateTag;
    private Vector2 _leftPressPos;
    private bool _leftPressActive;

    public MapLayerFlags Layers { get; } = new();
    public bool EditorInputMode { get; set; }

    public event Action<string>? StateSelected;
    public event Action<string, string>? ProvinceSelected;
    public event Action<ProvinceSnapshot?>? ProvinceHovered;
    public event Action<Vector2, MouseButton, bool>? EditorCanvasClicked;
    public event Action<Vector2>? EditorCanvasDragged;

    public MapLensMode CurrentLens => _currentLens;
    public string? SelectedProvinceId => _provByNumIdx.TryGetValue(_selectedProvNum, out var p) ? p.ProvinceId : null;
    public string? SelectedStateId => _provByNumIdx.TryGetValue(_selectedProvNum, out var p) ? p.StateId : null;
    public PlanarMapTopology? CurrentTopology => _topology;
    public BakedMapArtifact? CurrentBaked => _baked;

    public override void _Ready()
    {
        var edgeShader = GD.Load<Shader>("res://Shaders/edge_terra_incognita.gdshader");
        _edgeFogMaterial = new ShaderMaterial { Shader = edgeShader };
        _edgeFogRect = new ColorRect
        {
            Position = new Vector2(-1400f, -1000f),
            Size = new Vector2(4720f, 3120f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = _edgeFogMaterial
        };
        AddChild(_edgeFogRect);

        var shader = GD.Load<Shader>("res://Shaders/map_parchment_lenses.gdshader");
        _mapMaterial = new ShaderMaterial { Shader = shader };

        _mapRect = new TextureRect
        {
            Position = Vector2.Zero,
            Size = WorldCanvasSize,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = _mapMaterial
        };
        AddChild(_mapRect);

        var borderShader = GD.Load<Shader>("res://Shaders/map_borders.gdshader");
        _borderMaterial = new ShaderMaterial { Shader = borderShader };
        _borderMeshNode = new MeshInstance2D { Material = _borderMaterial };
        AddChild(_borderMeshNode);

        _overlay = new MapOverlayCanvas(this);
        AddChild(_overlay);
    }

    public override void _Process(double delta)
    {
        _elapsedSeconds += (float)delta;
        _edgeFogMaterial?.SetShaderParameter("time_seconds", _elapsedSeconds);
        if (_baked is not null)
            _mapMaterial.SetShaderParameter("time_seconds", _elapsedSeconds);
    }

    public void ConfigureEnvironment(GeographyFeaturesData? features, string? cacheDirectory = null)
    {
        _features = features ?? GeographyFeaturesData.Empty;
        _vmapCacheDir = cacheDirectory;
        RebuildFeatureCaches();
    }

    public void SetCameraZoom(float zoom)
    {
        _cameraZoom = Mathf.Clamp(zoom, MinCameraZoom, MaxCameraZoom);
        _edgeFogMaterial?.SetShaderParameter("camera_zoom", _cameraZoom);
        _mapMaterial.SetShaderParameter("camera_zoom", _cameraZoom);
        _borderMaterial.SetShaderParameter("camera_zoom", _cameraZoom);
        _overlay.QueueRedraw();
    }

    public void ApplyLayerVisibility()
    {
        _mapMaterial?.SetShaderParameter("terrain_relief_strength", Layers.ShowTerrainRelief ? 1.0f : 0.0f);
        _mapMaterial?.SetShaderParameter("shader_border_strength", Layers.ShowBorders ? 0.35f : 0.0f);
        _borderMaterial?.SetShaderParameter("show_borders", Layers.ShowBorders);
        _borderMaterial?.SetShaderParameter("show_province_borders", Layers.ShowProvinceBorders);
        _overlay?.QueueRedraw();
    }

    public void ResetMap()
    {
        _hoveredProvNum = 0;
        _selectedProvNum = 0;
        _selectedStateTag = 0;
        _baked = null;
        _topology = null;
        _fullLookupDirty = true;
        _mapMaterial?.SetShaderParameter("hovered_province_idx", 0);
        _mapMaterial?.SetShaderParameter("selected_province_idx", 0);
        _mapMaterial?.SetShaderParameter("selected_state_tag", 0);
        _borderMaterial?.SetShaderParameter("hovered_province_idx", 0);
        _borderMaterial?.SetShaderParameter("selected_province_idx", 0);
        _overlay?.QueueRedraw();
    }

    public void SetLensMode(MapLensMode lens)
    {
        _currentLens = lens;
        _mapMaterial.SetShaderParameter("is_political_lens", lens == MapLensMode.Political);
        _fullLookupDirty = true;
        if (_latestSnapshot is not null)
            UpdateLookupTexture(_latestSnapshot);
        _overlay.QueueRedraw();
    }

    public void ApplySnapshot(RenderSnapshot snapshot)
    {
        _latestSnapshot = snapshot;

        _provByNumIdx.Clear();
        _provSpatialGrid.Clear();
        foreach (var p in snapshot.Provinces)
        {
            _provByNumIdx[p.NumericalIndex] = p;
            _provSpatialGrid.Add(p.AnchorX, p.AnchorY, p);
        }

        _stateById.Clear();
        _stateTagById.Clear();
        for (var i = 0; i < snapshot.States.Count; i++)
        {
            var s = snapshot.States[i];
            _stateById[s.StateId] = s;
            _stateTagById[s.StateId] = i + 1;
            if (!_nationTagById.ContainsKey(s.OwnerNationId))
                _nationTagById[s.OwnerNationId] = _nationTagById.Count + 1;
        }

        _shortNationNameCache.Clear();
        _shortNationNameCache[snapshot.PlayerNationId] = ExtractShortName(snapshot.PlayerNationName);
        foreach (var d in snapshot.PlayerDiplomacy)
            _shortNationNameCache[d.TargetNationId] = ExtractShortName(d.TargetNationName);

        if (_baked is null)
        {
            BuildStaticMapTextures(snapshot, _topology);
            RebuildRoadArcCache(snapshot);
        }

        _fullLookupDirty = true;
        UpdateLookupTexture(snapshot);
        _overlay.QueueRedraw();
    }

    /// <summary>
    /// Live re-bake used by the Map Editor whenever topology, provinces, owners, or terrain features change.
    /// </summary>
    public void RebuildFromAtlas(EditableWorldAtlas atlas, RenderSnapshot? syntheticSnapshot = null)
    {
        _topology = atlas.Topology;
        _features = atlas.Features;
        RebuildFeatureCaches();

        var snap = syntheticSnapshot ?? BuildSyntheticSnapshotFromAtlas(atlas);
        _latestSnapshot = snap;

        _provByNumIdx.Clear();
        _provSpatialGrid.Clear();
        foreach (var p in snap.Provinces)
        {
            _provByNumIdx[p.NumericalIndex] = p;
            _provSpatialGrid.Add(p.AnchorX, p.AnchorY, p);
        }

        _stateById.Clear();
        _stateTagById.Clear();
        for (var i = 0; i < snap.States.Count; i++)
        {
            var s = snap.States[i];
            _stateById[s.StateId] = s;
            _stateTagById[s.StateId] = i + 1;
            if (!_nationTagById.ContainsKey(s.OwnerNationId))
                _nationTagById[s.OwnerNationId] = _nationTagById.Count + 1;
        }

        _shortNationNameCache.Clear();
        foreach (var n in atlas.Nations)
            _shortNationNameCache[n.Id] = ExtractShortName(n.Name);

        BuildStaticMapTextures(snap, atlas.Topology, bypassCache: true);
        RebuildRoadArcCache(snap);
        _fullLookupDirty = true;
        UpdateLookupTexture(snap);
        _overlay.QueueRedraw();
    }

    private static string ExtractShortName(string full)
    {
        var idx = full.IndexOfAny(['(', '—']);
        var trimmed = idx >= 0 ? full[..idx].Trim() : full.Trim();
        return trimmed.ToUpperInvariant();
    }

    public bool TrySelectProvinceById(string provinceId)
    {
        foreach (var kv in _provByNumIdx)
        {
            if (kv.Value.ProvinceId == provinceId)
            {
                SelectProvinceInternal(kv.Value);
                return true;
            }
        }
        return false;
    }

    public bool TrySelectStateCapital(string stateId)
    {
        ProvinceSnapshot? fallback = null;
        foreach (var kv in _provByNumIdx)
        {
            if (kv.Value.StateId != stateId)
                continue;
            fallback ??= kv.Value;
            if (kv.Value.IsCapital)
            {
                SelectProvinceInternal(kv.Value);
                return true;
            }
        }
        if (fallback is not null)
        {
            SelectProvinceInternal(fallback);
            return true;
        }
        return false;
    }

    public Vector2 GetProvinceWorldPosition(string provinceId)
    {
        if (_baked is not null)
        {
            foreach (var kv in _provByNumIdx)
            {
                if (kv.Value.ProvinceId == provinceId &&
                    _baked.ProvinceCentroids.TryGetValue(kv.Key, out var c))
                {
                    return new Vector2(c.X * WorldCanvasSize.X, c.Y * WorldCanvasSize.Y);
                }
            }
        }
        return WorldCanvasSize * 0.5f;
    }

    public bool TryGetStateCenter(string stateId, out Vector2 worldPos)
    {
        if (_baked is not null && _baked.StateCentroids.TryGetValue(stateId, out var c))
        {
            worldPos = new Vector2(c.X * WorldCanvasSize.X, c.Y * WorldCanvasSize.Y);
            return true;
        }
        worldPos = WorldCanvasSize * 0.5f;
        return false;
    }

    public void SetSelectedState(string stateId) => TrySelectStateCapital(stateId);

    public bool TryClickWorldPosition(Vector2 worldPos)
    {
        var idx = LookupProvinceAtWorldPos(worldPos);
        if (idx <= 0 || !_provByNumIdx.TryGetValue(idx, out var prov))
            return false;

        SelectProvinceInternal(prov);
        return true;
    }

    public int LookupProvinceIndexAtWorld(Vector2 worldPos) => LookupProvinceAtWorldPos(worldPos);

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || _baked is null)
            return;

        if (@event is InputEventMouseMotion)
        {
            var worldPos = GetGlobalMousePosition();
            if (EditorInputMode && Input.IsMouseButtonPressed(MouseButton.Left))
                EditorCanvasDragged?.Invoke(worldPos);

            var idx = LookupProvinceAtWorldPos(worldPos);
            if (idx != _hoveredProvNum)
            {
                _hoveredProvNum = idx;
                _mapMaterial.SetShaderParameter("hovered_province_idx", _hoveredProvNum);
                _borderMaterial.SetShaderParameter("hovered_province_idx", _hoveredProvNum);
                ProvinceHovered?.Invoke(_hoveredProvNum > 0 && _provByNumIdx.TryGetValue(_hoveredProvNum, out var hp) ? hp : null);
                _overlay.QueueRedraw();
            }
            return;
        }

        if (@event is InputEventMouseButton mb)
        {
            if (EditorInputMode && mb.ButtonIndex == MouseButton.Left)
            {
                var wp = GetGlobalMousePosition();
                EditorCanvasClicked?.Invoke(wp, MouseButton.Left, mb.Pressed);
                GetViewport().SetInputAsHandled();
                return;
            }

            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _leftPressActive = true;
                    _leftPressPos = mb.Position;
                }
                else if (_leftPressActive)
                {
                    _leftPressActive = false;
                    if (_leftPressPos.DistanceTo(mb.Position) <= 8.0f)
                    {
                        var worldPos = GetGlobalMousePosition();
                        if (TryClickWorldPosition(worldPos))
                            GetViewport().SetInputAsHandled();
                    }
                }
            }
        }
    }

    private int LookupProvinceAtWorldPos(Vector2 worldPos)
    {
        if (_baked is null)
            return 0;

        var nx = worldPos.X / WorldCanvasSize.X;
        var ny = worldPos.Y / WorldCanvasSize.Y;
        if (nx < 0f || nx >= 1f || ny < 0f || ny >= 1f)
            return 0;

        var px = Math.Clamp((int)(nx * _baked.Width), 0, _baked.Width - 1);
        var py = Math.Clamp((int)(ny * _baked.Height), 0, _baked.Height - 1);
        return _baked.ProvinceIdGrid[py * _baked.Width + px];
    }

    private void SelectProvinceInternal(ProvinceSnapshot prov)
    {
        _selectedProvNum = prov.NumericalIndex;
        _selectedStateTag = _stateTagById.GetValueOrDefault(prov.StateId, 0);
        _mapMaterial.SetShaderParameter("selected_province_idx", _selectedProvNum);
        _mapMaterial.SetShaderParameter("selected_state_tag", _selectedStateTag);
        _borderMaterial.SetShaderParameter("selected_province_idx", _selectedProvNum);
        _overlay.QueueRedraw();

        ProvinceSelected?.Invoke(prov.ProvinceId, prov.StateId);
        StateSelected?.Invoke(prov.StateId);
    }

    private void BuildStaticMapTextures(RenderSnapshot snapshot, PlanarMapTopology? prebuiltTopology = null, bool bypassCache = false)
    {
        _topology = prebuiltTopology ?? PlanarTopologyBuilder.BuildFromProvinces(snapshot.Provinces, _features);
        var hash = MapBakePipeline.ComputeTopologyHash(_topology, _features, MapBakePipeline.DefaultWidth, MapBakePipeline.DefaultHeight);
        var cachePath = !bypassCache && _vmapCacheDir is not null
            ? Path.Combine(_vmapCacheDir, $"pontaria_{hash[..12]}.vmap")
            : null;

        BakedMapArtifact? artifact = cachePath is not null ? MapBakePipeline.TryLoadVmapCache(cachePath, hash) : null;
        if (artifact is null)
        {
            artifact = MapBakePipeline.BakeFromTopology(_topology, snapshot.Provinces, _features);
            if (cachePath is not null)
                MapBakePipeline.SaveVmapCache(cachePath, artifact);
        }

        _baked = artifact;

        var pImg = Image.CreateFromData(_baked.Width, _baked.Height, false, Image.Format.Rgb8, _baked.ProvinceDataRgb);
        _provinceDataTex = ImageTexture.CreateFromImage(pImg);

        var hImg = Image.CreateFromData(_baked.Width, _baked.Height, false, Image.Format.Rgb8, _baked.HeightCoastRgb);
        _heightCoastTex = ImageTexture.CreateFromImage(hImg);

        if (_lookupImage is null)
        {
            _lookupImage = Image.CreateEmpty(LookupWidth, LookupHeight, false, Image.Format.Rgba8);
            _lookupTex = ImageTexture.CreateFromImage(_lookupImage);
        }

        _mapRect.Texture = _provinceDataTex;
        _mapMaterial.SetShaderParameter("province_data_tex", _provinceDataTex);
        _mapMaterial.SetShaderParameter("height_coast_tex", _heightCoastTex);
        _mapMaterial.SetShaderParameter("province_lookup_tex", _lookupTex!);
        _mapMaterial.SetShaderParameter("camera_zoom", _cameraZoom);
        _mapMaterial.SetShaderParameter("is_political_lens", _currentLens == MapLensMode.Political);

        _borderMaterial.SetShaderParameter("province_lookup_tex", _lookupTex!);
        _borderMaterial.SetShaderParameter("camera_zoom", _cameraZoom);
        ApplyLayerVisibility();

        RebuildBorderArrayMesh(_baked);
    }

    private void RebuildBorderArrayMesh(BakedMapArtifact baked)
    {
        if (baked.BorderVertices.Length == 0 || baked.BorderIndices.Length == 0)
        {
            _borderMeshNode.Mesh = null;
            return;
        }

        var vCount = baked.BorderVertices.Length;
        var positions = new Vector2[vCount];
        var uvs = new Vector2[vCount];
        var colors = new Color[vCount];

        for (var i = 0; i < vCount; i++)
        {
            var bv = baked.BorderVertices[i];
            positions[i] = new Vector2(bv.X * WorldCanvasSize.X, bv.Y * WorldCanvasSize.Y);
            uvs[i] = new Vector2(bv.LeftFaceId, bv.RightFaceId);
            colors[i] = new Color(
                Math.Clamp(0.5f + 0.25f * bv.OffsetX, 0f, 1f),
                Math.Clamp(0.5f + 0.25f * bv.OffsetY, 0f, 1f),
                (bv.BorderFlags & 1) != 0 ? 1f : 0f,
                (i & 1) == 1 ? 1f : 0f);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = baked.BorderIndices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _borderMeshNode.Mesh = mesh;
    }

    public void MarkProvinceLookupDirty(int provNumericalIndex)
    {
        _dirtyLookupProvinces.Add(provNumericalIndex);
    }

    private void UpdateLookupTexture(RenderSnapshot snapshot)
    {
        if (_lookupImage is null || _lookupTex is null)
            return;

        if (_fullLookupDirty)
        {
            _lookupImage.SetPixel(0, 0, new Color(0.08f, 0.13f, 0.19f, 1.0f));
            _lookupImage.SetPixel(0, 1, new Color(0f, 0f, 0f, 0f));
            _lookupImage.SetPixel(0, 2, new Color(0f, 0f, 0f, 0f));

            foreach (var prov in snapshot.Provinces)
                WriteProvinceLookupEntry(prov);
            _dirtyLookupProvinces.Clear();
            _fullLookupDirty = false;
        }
        else if (_dirtyLookupProvinces.Count > 0)
        {
            foreach (var idx in _dirtyLookupProvinces)
            {
                if (_provByNumIdx.TryGetValue(idx, out var prov))
                    WriteProvinceLookupEntry(prov);
            }
            _dirtyLookupProvinces.Clear();
        }
        else
        {
            return;
        }

        _lookupTex.Update(_lookupImage);
    }

    private void WriteProvinceLookupEntry(ProvinceSnapshot prov)
    {
        if (_lookupImage is null || prov.NumericalIndex <= 0 || prov.NumericalIndex > 65535)
            return;

        var p = prov.NumericalIndex;
        var px = p & 255;
        var pyBase = (p >> 8) * 3;

        var state = _stateById.GetValueOrDefault(prov.StateId);
        var lensCol = ResolveLensColor(prov, state);

        var nationTag = _nationTagById.GetValueOrDefault(prov.OwnerNationId, 1);
        var stateTag = _stateTagById.GetValueOrDefault(prov.StateId, 1);
        var occupiedFlag = prov.OccupiedByNationId is not null ? 1.0f : 0.0f;
        var occupierHue = prov.OccupiedByNationId is not null && _nationTagById.TryGetValue(prov.OccupiedByNationId, out var occTag)
            ? (occTag % 16) / 15.0f
            : 0.0f;

        _lookupImage.SetPixel(px, pyBase + 0, lensCol);
        _lookupImage.SetPixel(px, pyBase + 1, new Color(
            (nationTag & 255) / 255.0f,
            ((nationTag >> 8) & 255) / 255.0f,
            (stateTag & 255) / 255.0f,
            ((stateTag >> 8) & 255) / 255.0f));
        _lookupImage.SetPixel(px, pyBase + 2, new Color(occupiedFlag, occupierHue, 0f, 1f));
    }

    private void RebuildFeatureCaches()
    {
        _cachedRiverPolylines.Clear();
        foreach (var r in _features.Rivers)
        {
            if (r.ControlPoints.Length < 2) continue;
            if (r.ControlPoints.Length >= 4)
            {
                var pts = new Vector2[25];
                for (var i = 0; i <= 24; i++)
                {
                    var t = i / 24f;
                    var p0 = new Vector2(r.ControlPoints[0].X, r.ControlPoints[0].Y);
                    var p1 = new Vector2(r.ControlPoints[1].X, r.ControlPoints[1].Y);
                    var p2 = new Vector2(r.ControlPoints[2].X, r.ControlPoints[2].Y);
                    var p3 = new Vector2(r.ControlPoints[3].X, r.ControlPoints[3].Y);
                    var inv = 1f - t;
                    var p = inv * inv * inv * p0 + 3f * inv * inv * t * p1 + 3f * inv * t * t * p2 + t * t * t * p3;
                    pts[i] = new Vector2(p.X * WorldCanvasSize.X, p.Y * WorldCanvasSize.Y);
                }
                _cachedRiverPolylines.Add(pts);
            }
            else
            {
                var pts = new Vector2[r.ControlPoints.Length];
                for (var i = 0; i < pts.Length; i++)
                    pts[i] = new Vector2(r.ControlPoints[i].X * WorldCanvasSize.X, r.ControlPoints[i].Y * WorldCanvasSize.Y);
                _cachedRiverPolylines.Add(pts);
            }
        }

        _cachedMountainPolylines.Clear();
        foreach (var mr in _features.MountainRanges)
        {
            if (mr.SpinePoints.Length < 2) continue;
            var pts = new Vector2[mr.SpinePoints.Length];
            for (var i = 0; i < pts.Length; i++)
                pts[i] = new Vector2(mr.SpinePoints[i].X * WorldCanvasSize.X, mr.SpinePoints[i].Y * WorldCanvasSize.Y);
            _cachedMountainPolylines.Add(pts);
        }
    }

    private void RebuildRoadArcCache(RenderSnapshot snap)
    {
        _cachedRoadArcs.Clear();
        if (_baked is null)
            return;

        var drawnPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var st in snap.States)
        {
            if (!_baked.StateCentroids.TryGetValue(st.StateId, out var cA))
                continue;
            var posA = new Vector2(cA.X * WorldCanvasSize.X, cA.Y * WorldCanvasSize.Y);

            foreach (var adjId in st.AdjacentStateIds)
            {
                var key = string.CompareOrdinal(st.StateId, adjId) < 0 ? $"{st.StateId}|{adjId}" : $"{adjId}|{st.StateId}";
                if (!drawnPairs.Add(key) || !_baked.StateCentroids.TryGetValue(adjId, out var cB))
                    continue;

                var posB = new Vector2(cB.X * WorldCanvasSize.X, cB.Y * WorldCanvasSize.Y);
                if (posA.DistanceTo(posB) > 290f)
                    continue;
                var mid = (posA + posB) * 0.5f;
                var normal = (posB - posA).Orthogonal().Normalized();
                var ctrl = mid + normal * (posA.DistanceTo(posB) * 0.12f);

                var pts = new Vector2[13];
                for (var i = 0; i <= 12; i++)
                {
                    var t = i / 12f;
                    var inv = 1f - t;
                    pts[i] = inv * inv * posA + 2f * inv * t * ctrl + t * t * posB;
                }
                _cachedRoadArcs.Add((pts, 0, 0, st.StateId, adjId));
            }
        }
    }

    private static RenderSnapshot BuildSyntheticSnapshotFromAtlas(EditableWorldAtlas atlas)
    {
        var provSnaps = atlas.BuildProvinceSnapshots();
        var nationById = atlas.Nations.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var stateSnaps = new List<StateSnapshot>(atlas.States.Count);

        foreach (var st in atlas.States)
        {
            var nat = nationById.GetValueOrDefault(st.OwnerNationId);
            var colHex = nat?.MapColorHex ?? "#6B675E";
            var det = st.EnsureProvinceDetails();
            stateSnaps.Add(new StateSnapshot(
                st.Id,
                st.Name,
                st.OwnerNationId,
                colHex,
                (float)st.MapX,
                (float)st.MapY,
                (float)st.Radius,
                st.ArableLand,
                st.InitialPops.Sum(p => p.Population),
                10f,
                1.0,
                "#78909C",
                0,
                0,
                false,
                false,
                false,
                false,
                st.AdjacentStateIds,
                st.Provinces,
                det.Select(d => d.Id).ToArray(),
                Array.Empty<string>(),
                Array.Empty<BuildingSnapshot>(),
                st.RegionId,
                st.SuperRegionId,
                null));
        }

        return new RenderSnapshot(
            new GameDate(1380, 1, 1, 0),
            true,
            2,
            atlas.Nations.FirstOrDefault()?.Id ?? "NONE",
            atlas.Nations.FirstOrDefault()?.Name ?? "Editor",
            MonetaryAmount.Zero,
            MonetaryAmount.Zero,
            0.0, 0, 0, 0, "", "", "",
            0, 0, 0, 0,
            null, 0.0, null, 0.0,
            new Dictionary<string, double>(),
            stateSnaps,
            provSnaps,
            Array.Empty<InterestGroupSnapshot>(),
            Array.Empty<LawStatusSnapshot>(),
            Array.Empty<TechnologyProgressSnapshot>(),
            Array.Empty<DiplomaticRelationSnapshot>(),
            Array.Empty<JournalEntrySnapshot>(),
            Array.Empty<PopSummarySnapshot>());
    }

    private Color ResolveLensColor(ProvinceSnapshot prov, StateSnapshot? state)
    {
        if (state is null)
            return new Color(0.5f, 0.5f, 0.5f);

        var lensColor = _currentLens switch
        {
            MapLensMode.Political => Color.FromHtml(state.OwnerColorHex),
            MapLensMode.Ancestry => Color.FromHtml(state.DominantAncestryColorHex),
            MapLensMode.StandardOfLiving => SolHeatmapColor(state.AverageSoL),
            MapLensMode.Economic => EconomicHeatmapColor(state.FarmLevels + (prov.HoldingLevel - 1) * 2),
            MapLensMode.Diplomacy => prov.OwnerNationId == _latestSnapshot?.PlayerNationId
                ? new Color(0.24f, 0.52f, 0.78f)
                : state.IsAtWarWithPlayer
                    ? new Color(0.80f, 0.16f, 0.16f)
                    : state.IsAlliedWithPlayer
                        ? new Color(0.20f, 0.70f, 0.44f)
                        : state.HasTradePactWithPlayer
                            ? new Color(0.84f, 0.66f, 0.24f)
                            : state.DiplomacyScoreWithPlayer < 0
                                ? new Color(0.62f, 0.30f, 0.24f)
                                : new Color(0.45f, 0.48f, 0.44f),
            _ => Color.FromHtml(state.OwnerColorHex)
        };

        var mod = 0.94f + ((prov.NumericalIndex % 5) - 2) * 0.025f;
        return new Color(
            Mathf.Clamp(lensColor.R * mod, 0.05f, 0.96f),
            Mathf.Clamp(lensColor.G * mod, 0.05f, 0.96f),
            Mathf.Clamp(lensColor.B * mod, 0.05f, 0.96f),
            1.0f);
    }

    private static Color SolHeatmapColor(float sol)
    {
        var t = Mathf.Clamp((sol - 5f) / 18f, 0f, 1f);
        return new Color(0.75f * (1f - t), 0.24f + 0.66f * t, 0.20f);
    }

    private static Color EconomicHeatmapColor(int levelScore)
    {
        var t = Mathf.Clamp(levelScore / 18f, 0f, 1f);
        return new Color(0.26f, 0.40f + 0.48f * t, 0.62f * t + 0.18f);
    }

    private sealed partial class MapOverlayCanvas : Node2D
    {
        private readonly MapView2D _owner;

        public MapOverlayCanvas(MapView2D owner)
        {
            _owner = owner;
        }

        public override void _Draw()
        {
            var baked = _owner._baked;
            var snap = _owner._latestSnapshot;
            if (baked is null || snap is null)
                return;

            var zoom = _owner._cameraZoom;
            var font = ThemeDB.Singleton.FallbackFont;
            var invScale = Mathf.Clamp(1.0f / Mathf.Sqrt(zoom), 0.45f, 1.30f);

            // Compute normalized viewport frustum for fast SpatialGrid2D culling
            var canvasInv = GetCanvasTransform().AffineInverse();
            var vpSize = GetViewportRect().Size;
            var topLeft = (canvasInv * Vector2.Zero) / WorldCanvasSize;
            var botRight = (canvasInv * vpSize) / WorldCanvasSize;
            var minX = Math.Clamp(Math.Min(topLeft.X, botRight.X) - 0.06f, 0f, 1f);
            var minY = Math.Clamp(Math.Min(topLeft.Y, botRight.Y) - 0.06f, 0f, 1f);
            var maxX = Math.Clamp(Math.Max(topLeft.X, botRight.X) + 0.06f, 0f, 1f);
            var maxY = Math.Clamp(Math.Max(topLeft.Y, botRight.Y) + 0.06f, 0f, 1f);

            if (_owner.Layers.ShowRivers)
                DrawCachedRivers(invScale, zoom);
            DrawEasternBastionAndVeasperiaMist(font, invScale, zoom);
            if (_owner.Layers.ShowRoads)
                DrawCachedStateRoads(invScale, zoom);
            if (_owner.Layers.ShowHoldings)
                DrawCulledProvinceHoldings(baked, font, invScale, zoom, minX, minY, maxX, maxY);
            if (_owner.Layers.ShowLabels)
                DrawHierarchyLabels(baked, snap, font, invScale, zoom);
        }

        private void DrawCachedRivers(float invScale, float zoom)
        {
            var riverColor = new Color(0.16f, 0.34f, 0.48f, zoom >= 1.2f ? 0.78f : 0.55f);
            var bankHighlight = new Color(0.08f, 0.14f, 0.20f, 0.35f);
            var list = _owner._cachedRiverPolylines;
            for (var i = 0; i < list.Count; i++)
            {
                var pts = list[i];
                DrawPolyline(pts, bankHighlight, 3.2f * invScale, antialiased: true);
                DrawPolyline(pts, riverColor, 1.8f * invScale, antialiased: true);
            }
        }

        private void DrawEasternBastionAndVeasperiaMist(Font font, float invScale, float zoom)
        {
            // Draw jagged alpine ridge chevrons across impassable Cordilheiras Mortas so the inhospitable choke is legible
            foreach (var mr in _owner._features.MountainRanges)
            {
                if (!mr.IsImpassable || mr.SpinePoints.Length < 2)
                    continue;

                var ridgeInk = new Color(0.18f, 0.15f, 0.16f, 0.72f);
                var ridgeSnow = new Color(0.72f, 0.68f, 0.65f, 0.45f);
                for (var s = 0; s < mr.SpinePoints.Length - 1; s++)
                {
                    var a = new Vector2(mr.SpinePoints[s].X * WorldCanvasSize.X, mr.SpinePoints[s].Y * WorldCanvasSize.Y);
                    var b = new Vector2(mr.SpinePoints[s + 1].X * WorldCanvasSize.X, mr.SpinePoints[s + 1].Y * WorldCanvasSize.Y);
                    var steps = 3;
                    for (var k = 0; k < steps; k++)
                    {
                        var t = (k + 0.5f) / steps;
                        var p = a.Lerp(b, t);
                        var r = 6.5f * invScale;
                        DrawPolyline(
                            [p + new Vector2(-r, r * 0.6f), p + new Vector2(0f, -r * 0.85f), p + new Vector2(r, r * 0.6f)],
                            ridgeInk, 2.2f * invScale, antialiased: true);
                        DrawPolyline(
                            [p + new Vector2(-r * 0.45f, -r * 0.1f), p + new Vector2(0f, -r * 0.85f), p + new Vector2(r * 0.45f, -r * 0.1f)],
                            ridgeSnow, 1.3f * invScale, antialiased: true);
                    }
                }
            }

            // Draw the monumental Muralha Bastion sealing the straight Valmetia Land Strait (x = 0.9225)
            var wallX = 0.9225f * WorldCanvasSize.X;
            var wallTop = new Vector2(wallX, 0.431f * WorldCanvasSize.Y);
            var wallBot = new Vector2(wallX, 0.471f * WorldCanvasSize.Y);
            var wallW = Mathf.Max(3.8f, 7.6f * invScale);

            DrawLine(wallTop + new Vector2(2.5f * invScale, 1.5f * invScale), wallBot + new Vector2(2.5f * invScale, 1.5f * invScale),
                new Color(0.04f, 0.03f, 0.04f, 0.85f), wallW * 1.45f, antialiased: true);
            DrawLine(wallTop, wallBot, new Color(0.28f, 0.25f, 0.29f, 0.98f), wallW, antialiased: true);
            DrawLine(wallTop + new Vector2(-wallW * 0.18f, 0f), wallBot + new Vector2(-wallW * 0.18f, 0f),
                new Color(0.62f, 0.58f, 0.54f, 0.92f), wallW * 0.35f, antialiased: true);

            for (var bIdx = 0; bIdx < 5; bIdx++)
            {
                var pt = wallTop.Lerp(wallBot, bIdx / 4f);
                var towerR = wallW * 0.68f;
                DrawRect(new Rect2(pt - new Vector2(towerR, towerR), new Vector2(towerR * 2f, towerR * 2f)),
                    new Color(0.18f, 0.16f, 0.19f, 1.0f));
                DrawRect(new Rect2(pt - new Vector2(towerR * 0.55f, towerR * 0.55f), new Vector2(towerR * 1.1f, towerR * 1.1f)),
                    bIdx == 2 ? new Color(0.95f, 0.62f, 0.22f, 0.95f) : new Color(0.75f, 0.68f, 0.55f, 0.85f));
            }

            // Soft cartographic mist gradient along right edge + atmospheric labels
            if (_owner.Layers.ShowLabels && zoom >= 0.72f)
            {
                var alpha = Mathf.Clamp((zoom - 0.72f) / 0.30f, 0f, 1f);
                var bSize = Mathf.Clamp((int)(11f * invScale), 9, 15);
                var bPos = wallTop + new Vector2(-106f * invScale, -10f * invScale);
                DrawStringOutline(font, bPos, "MURALHA BASTION", HorizontalAlignment.Left, -1, bSize,
                    Mathf.Max(2, (int)(3 * invScale)), new Color(0.05f, 0.04f, 0.04f, 0.90f * alpha));
                DrawString(font, bPos, "MURALHA BASTION", HorizontalAlignment.Left, -1, bSize,
                    new Color(0.95f, 0.80f, 0.48f, 0.95f * alpha));

                var vSize = Mathf.Clamp((int)(12f * invScale), 9, 16);
                var vPos = new Vector2(0.945f * WorldCanvasSize.X, 0.525f * WorldCanvasSize.Y);
                DrawStringOutline(font, vPos, "VEÂSPERIA", HorizontalAlignment.Left, -1, vSize,
                    Mathf.Max(2, (int)(3 * invScale)), new Color(0.03f, 0.03f, 0.04f, 0.92f * alpha));
                DrawString(font, vPos, "VEÂSPERIA", HorizontalAlignment.Left, -1, vSize,
                    new Color(0.84f, 0.87f, 0.94f, 0.92f * alpha));
            }
        }

        private void DrawCachedStateRoads(float invScale, float zoom)
        {
            if (zoom < 1.05f)
                return;

            var arcs = _owner._cachedRoadArcs;
            var states = _owner._stateById;
            var alphaBoost = Mathf.Clamp((zoom - 1.05f) / 0.35f, 0f, 1f);
            for (var i = 0; i < arcs.Count; i++)
            {
                var (pts, _, _, idA, idB) = arcs[i];
                states.TryGetValue(idA, out var stA);
                states.TryGetValue(idB, out var stB);
                var war = (stA?.IsAtWarWithPlayer ?? false) || (stB?.IsAtWarWithPlayer ?? false);
                var trade = (stA is not null && stB is not null) &&
                            ((stA.HasTradePactWithPlayer && stA.OwnerNationId != stB.OwnerNationId) ||
                             (stB.HasTradePactWithPlayer && stA.OwnerNationId != stB.OwnerNationId));

                var col = war
                    ? new Color(0.85f, 0.24f, 0.18f, 0.45f * alphaBoost)
                    : trade
                        ? new Color(0.90f, 0.72f, 0.30f, 0.42f * alphaBoost)
                        : new Color(0.24f, 0.19f, 0.13f, 0.28f * alphaBoost);

                DrawPolyline(pts, col, 1.5f * invScale, antialiased: true);
            }
        }

        private void DrawCulledProvinceHoldings(
            BakedMapArtifact baked,
            Font font,
            float invScale,
            float zoom,
            float minX,
            float minY,
            float maxX,
            float maxY)
        {
            var visible = _owner._visibleProvScratch;
            _owner._provSpatialGrid.QueryRect(minX, minY, maxX, maxY, visible);

            for (var i = 0; i < visible.Count; i++)
            {
                var prov = visible[i];
                if (!baked.ProvinceCentroids.TryGetValue(prov.NumericalIndex, out var centroid))
                    continue;

                var isMajor = prov.IsCapital || prov.HoldingLevel >= 3;
                var isSelected = prov.NumericalIndex == _owner._selectedProvNum;
                var isHovered = prov.NumericalIndex == _owner._hoveredProvNum;

                if (!isMajor && !isSelected && !isHovered && zoom < 0.90f)
                    continue;

                var alpha = isMajor || isSelected || isHovered
                    ? 1.0f
                    : Mathf.Clamp((zoom - 0.88f) / 0.38f, 0f, 1f);

                var pos = new Vector2(centroid.X * WorldCanvasSize.X, centroid.Y * WorldCanvasSize.Y);
                var s = (prov.IsCapital ? 7.4f : 5.4f + Mathf.Min(2.4f, (prov.HoldingLevel - 1) * 0.45f)) * invScale;

                if (isSelected)
                {
                    DrawCircle(pos, s * 1.95f, new Color(0.99f, 0.84f, 0.30f, 0.34f));
                    DrawArc(pos, s * 1.95f, 0f, Mathf.Tau, 24, new Color(0.99f, 0.88f, 0.38f, 0.95f), 1.8f * invScale);
                }
                else if (isHovered)
                {
                    DrawArc(pos, s * 1.65f, 0f, Mathf.Tau, 20, new Color(0.98f, 0.96f, 0.82f, 0.85f), 1.4f * invScale);
                }

                DrawCircle(pos + new Vector2(1.2f, 1.5f) * invScale, s * 1.15f, new Color(0.03f, 0.03f, 0.04f, 0.55f * alpha));
                DrawCircle(pos, s * 1.10f, new Color(0.11f, 0.13f, 0.17f, 0.92f * alpha));

                var iconColor = prov.HubType switch
                {
                    "castle" => new Color(0.86f, 0.78f, 0.62f, alpha),
                    "city" => new Color(0.95f, 0.81f, 0.36f, alpha),
                    "temple" => new Color(0.74f, 0.86f, 0.96f, alpha),
                    "farm" => new Color(0.78f, 0.85f, 0.38f, alpha),
                    "mine" => new Color(0.76f, 0.60f, 0.88f, alpha),
                    "wood" => new Color(0.45f, 0.80f, 0.48f, alpha),
                    "port" => new Color(0.42f, 0.80f, 0.94f, alpha),
                    _ => new Color(0.88f, 0.84f, 0.72f, alpha)
                };

                DrawHoldingGlyph(pos, s * 0.75f, prov.HubType, iconColor, prov.IsCapital);
                DrawArc(pos, s * 1.10f, 0f, Mathf.Tau, 18, prov.IsCapital
                    ? new Color(0.98f, 0.82f, 0.32f, 0.95f * alpha)
                    : new Color(0.55f, 0.50f, 0.40f, 0.78f * alpha), 1.2f * invScale);

                if (prov.FortLevel > 0)
                {
                    var fPos = pos + new Vector2(s * 0.95f, -s * 0.85f);
                    DrawRect(new Rect2(fPos - new Vector2(s * 0.38f, s * 0.38f), new Vector2(s * 0.76f, s * 0.76f)), new Color(0.20f, 0.22f, 0.26f, alpha));
                    DrawRect(new Rect2(fPos - new Vector2(s * 0.38f, s * 0.38f), new Vector2(s * 0.76f, s * 0.76f)), new Color(0.90f, 0.76f, 0.42f, alpha), false, 1.0f * invScale);
                }

                if (zoom >= 1.12f || isSelected || isHovered)
                {
                    var labelAlpha = isSelected || isHovered ? 1.0f : Mathf.Clamp((zoom - 1.12f) / 0.34f, 0f, 1f);
                    var fontSize = Mathf.Clamp((int)(12.5f * invScale), 9, 17);
                    var label = prov.DynamicDisplayName ?? prov.Name;
                    var textOffset = new Vector2(-font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize).X * 0.5f, s * 2.35f + fontSize * 0.7f);
                    var textCol = isSelected
                        ? new Color(1.0f, 0.93f, 0.62f, labelAlpha)
                        : new Color(0.94f, 0.92f, 0.86f, labelAlpha * 0.94f);
                    DrawStringOutline(font, pos + textOffset, label, HorizontalAlignment.Left, -1, fontSize, Mathf.Max(2, (int)(3 * invScale)), new Color(0.04f, 0.04f, 0.05f, 0.85f * labelAlpha));
                    DrawString(font, pos + textOffset, label, HorizontalAlignment.Left, -1, fontSize, textCol);
                }
            }
        }

        private void DrawHierarchyLabels(BakedMapArtifact baked, RenderSnapshot snap, Font font, float invScale, float zoom)
        {
            var nationAlpha = 1.0f - Mathf.Clamp((zoom - 1.35f) / 0.65f, 0f, 0.88f);
            if (nationAlpha > 0.08f)
            {
                var placedRects = new List<Rect2>(baked.NationLabelAnchors.Count);
                foreach (var (nationId, anchor) in baked.NationLabelAnchors.OrderByDescending(kv => kv.Value.SpanWidth))
                {
                    if (nationId == "NONE" || !_owner._shortNationNameCache.TryGetValue(nationId, out var shortName))
                        continue;

                    var basePos = new Vector2(anchor.X * WorldCanvasSize.X, anchor.Y * WorldCanvasSize.Y - 10f * invScale);
                    var fontSize = Mathf.Clamp((int)((11f + anchor.SpanWidth * 40f) * invScale), 10, 24);
                    var sz = font.GetStringSize(shortName, HorizontalAlignment.Left, -1, fontSize);
                    var origin = basePos - new Vector2(sz.X * 0.5f, 0f);

                    // De-overlap neighboring micro-state labels vertically
                    for (var step = 0; step < 5; step++)
                    {
                        var rect = new Rect2(origin - new Vector2(0, sz.Y * 0.85f), sz);
                        if (!placedRects.Any(r => r.Intersects(rect)))
                            break;
                        var dir = (step % 2 == 0 ? 1 : -1) * ((step / 2) + 1);
                        origin.Y = basePos.Y + dir * (sz.Y + 3f);
                    }
                    placedRects.Add(new Rect2(origin - new Vector2(0, sz.Y * 0.85f), sz));

                    DrawStringOutline(font, origin, shortName, HorizontalAlignment.Left, -1, fontSize, Mathf.Max(2, (int)(4 * invScale)), new Color(0.05f, 0.04f, 0.03f, 0.85f * nationAlpha));
                    DrawString(font, origin, shortName, HorizontalAlignment.Left, -1, fontSize, new Color(0.98f, 0.94f, 0.82f, 0.94f * nationAlpha));
                }
            }

            if (zoom >= 0.96f && zoom <= 2.45f)
            {
                var stAlpha = Mathf.Clamp((zoom - 0.96f) / 0.35f, 0f, 1f) * (1.0f - Mathf.Clamp((zoom - 1.95f) / 0.50f, 0f, 0.75f));
                foreach (var st in snap.States)
                {
                    if (!baked.StateCentroids.TryGetValue(st.StateId, out var centroid))
                        continue;

                    var pos = new Vector2(centroid.X * WorldCanvasSize.X, centroid.Y * WorldCanvasSize.Y - 18f * invScale);
                    var fontSize = Mathf.Clamp((int)(13f * invScale), 10, 17);
                    var sz = font.GetStringSize(st.Name, HorizontalAlignment.Left, -1, fontSize);
                    var origin = pos - new Vector2(sz.X * 0.5f, 0f);

                    DrawStringOutline(font, origin, st.Name, HorizontalAlignment.Left, -1, fontSize, Mathf.Max(2, (int)(3 * invScale)), new Color(0.04f, 0.04f, 0.05f, 0.78f * stAlpha));
                    DrawString(font, origin, st.Name, HorizontalAlignment.Left, -1, fontSize, new Color(0.84f, 0.89f, 0.95f, 0.88f * stAlpha));
                }
            }
        }

        private void DrawHoldingGlyph(Vector2 center, float r, string hubType, Color color, bool isCapital)
        {
            switch (hubType)
            {
                case "castle":
                    DrawRect(new Rect2(center - new Vector2(r * 0.65f, r * 0.55f), new Vector2(r * 1.30f, r * 1.15f)), color);
                    DrawRect(new Rect2(center - new Vector2(r * 0.80f, r * 0.85f), new Vector2(r * 0.42f, r * 0.40f)), color);
                    DrawRect(new Rect2(center + new Vector2(r * 0.38f, -r * 0.85f), new Vector2(r * 0.42f, r * 0.40f)), color);
                    break;
                case "city":
                    DrawRect(new Rect2(center - new Vector2(r * 0.85f, r * 0.15f), new Vector2(r * 0.55f, r * 0.75f)), color);
                    DrawRect(new Rect2(center - new Vector2(r * 0.25f, r * 0.75f), new Vector2(r * 0.58f, r * 1.35f)), color);
                    DrawRect(new Rect2(center + new Vector2(r * 0.36f, -r * 0.25f), new Vector2(r * 0.50f, r * 0.85f)), color);
                    break;
                case "temple":
                    DrawColoredPolygon(
                    [
                        center + new Vector2(0f, -r * 1.0f),
                        center + new Vector2(r * 0.75f, r * 0.65f),
                        center + new Vector2(-r * 0.75f, r * 0.65f)
                    ], color);
                    break;
                case "mine":
                    DrawColoredPolygon(
                    [
                        center + new Vector2(0f, -r * 0.90f),
                        center + new Vector2(r * 0.85f, 0f),
                        center + new Vector2(0f, r * 0.90f),
                        center + new Vector2(-r * 0.85f, 0f)
                    ], color);
                    break;
                case "port":
                    DrawLine(center + new Vector2(0f, -r * 0.90f), center + new Vector2(0f, r * 0.50f), color, Mathf.Max(1.5f, r * 0.28f));
                    DrawColoredPolygon(
                    [
                        center + new Vector2(-r * 0.85f, r * 0.25f),
                        center + new Vector2(r * 0.85f, r * 0.25f),
                        center + new Vector2(r * 0.50f, r * 0.85f),
                        center + new Vector2(-r * 0.50f, r * 0.85f)
                    ], color);
                    break;
                case "wood":
                    DrawColoredPolygon(
                    [
                        center + new Vector2(0f, -r * 0.95f),
                        center + new Vector2(r * 0.80f, r * 0.55f),
                        center + new Vector2(-r * 0.80f, r * 0.55f)
                    ], color);
                    break;
                default:
                    DrawCircle(center, r * 0.62f, color);
                    DrawLine(center - new Vector2(r * 0.85f, 0f), center + new Vector2(r * 0.85f, 0f), color, Mathf.Max(1.2f, r * 0.22f));
                    break;
            }

            if (isCapital)
            {
                var crownPos = center + new Vector2(0f, -r * 1.48f);
                DrawColoredPolygon(
                [
                    crownPos + new Vector2(0f, -r * 0.42f),
                    crownPos + new Vector2(r * 0.42f, 0f),
                    crownPos + new Vector2(0f, r * 0.42f),
                    crownPos + new Vector2(-r * 0.42f, 0f)
                ], new Color(1.0f, 0.88f, 0.32f, 1.0f));
            }
        }
    }
}
