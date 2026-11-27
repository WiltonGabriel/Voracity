using Godot;
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
/// Thin Godot 4 map coordinator delegating raster upload (MapTextureController),
/// batched border mesh generation (BorderMeshBuilder), GPU lens colors (MapLensColorizer),
/// world hit-testing (MapInputController), and zero-GC overlay rendering (MapOverlayRenderer).
/// </summary>
public partial class MapView2D : Node2D
{
    public static readonly Vector2 WorldCanvasSize = new(1920, 1120);
    public const float MinCameraZoom = 0.38f;
    public const float MaxCameraZoom = 4.80f;

    private ColorRect _edgeFogRect = null!;
    private ShaderMaterial _edgeFogMaterial = null!;
    private TextureRect _mapRect = null!;
    private ShaderMaterial _mapMaterial = null!;
    private MeshInstance2D _borderMeshNode = null!;
    private ShaderMaterial _borderMaterial = null!;
    private MapOverlayRenderer _overlay = null!;

    private readonly MapTextureController _textures = new();
    private readonly MapInputController _input = new(WorldCanvasSize);

    private readonly Dictionary<int, ProvinceSnapshot> _provByNumIdx = new();
    private readonly Dictionary<string, StateSnapshot> _stateById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nationTagById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _stateTagById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shortNationNameCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Color> _marketColors = new(StringComparer.Ordinal);
    private readonly HashSet<int> _dirtyLookupLocations = new();

    private string? _vmapCacheDir;
    private float _elapsedSeconds;
    private Vector2 _leftPressPos;
    private bool _leftPressActive;

    internal readonly SpatialGrid2D<ProvinceSnapshot> LocationSpatialGrid = new(32, 20);
    internal readonly List<Vector2[]> CachedRiverPolylines = new();
    internal readonly List<Vector2[]> CachedRoadArcs = new();
    internal readonly List<ProvinceSnapshot> VisibleLocationScratch = new(256);

    internal GeographyFeaturesData Features { get; private set; } = GeographyFeaturesData.Empty;
    internal RenderSnapshot? LatestSnapshot { get; private set; }
    internal float CameraZoom { get; private set; } = 1.0f;
    internal int HoveredProvNum { get; private set; }
    internal int SelectedProvNum { get; private set; }

    public MapLayerFlags Layers { get; } = new();
    public bool EditorInputMode { get; set; }

    public event Action<string>? StateSelected;
    public event Action<string, string>? ProvinceSelected;
    public event Action<ProvinceSnapshot?>? ProvinceHovered;
    public event Action<Vector2, MouseButton, bool>? EditorCanvasClicked;
    public event Action<Vector2>? EditorCanvasDragged;

    public MapLensMode CurrentLens { get; private set; } = MapLensMode.Political;
    public string? SelectedProvinceId => _provByNumIdx.TryGetValue(SelectedProvNum, out var p) ? p.ProvinceId : null;
    public string? SelectedStateId => _provByNumIdx.TryGetValue(SelectedProvNum, out var p) ? p.StateId : null;
    public PlanarMapTopology? CurrentTopology => _textures.CurrentTopology;
    public BakedMapArtifact? CurrentBaked => _textures.CurrentBaked;

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

        _overlay = new MapOverlayRenderer(this);
        AddChild(_overlay);
    }

    public override void _Process(double delta)
    {
        _elapsedSeconds += (float)delta;
        _edgeFogMaterial?.SetShaderParameter("time_seconds", _elapsedSeconds);
        if (_textures.CurrentBaked is not null)
            _mapMaterial.SetShaderParameter("time_seconds", _elapsedSeconds);
    }

    public void ConfigureEnvironment(GeographyFeaturesData? features, string? cacheDirectory = null)
    {
        Features = features ?? GeographyFeaturesData.Empty;
        _vmapCacheDir = cacheDirectory;
        RebuildFeatureCaches();
    }

    public void SetPrebuiltTopology(PlanarMapTopology? topology)
    {
        _textures.SetPrebuiltTopology(topology);
    }

    public void SetCameraZoom(float zoom)
    {
        CameraZoom = Mathf.Clamp(zoom, MinCameraZoom, MaxCameraZoom);
        _edgeFogMaterial?.SetShaderParameter("camera_zoom", CameraZoom);
        _mapMaterial.SetShaderParameter("camera_zoom", CameraZoom);
        _borderMaterial.SetShaderParameter("camera_zoom", CameraZoom);
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
        HoveredProvNum = 0;
        SelectedProvNum = 0;
        _textures.Reset();
        _mapMaterial?.SetShaderParameter("hovered_province_idx", 0);
        _mapMaterial?.SetShaderParameter("selected_province_idx", 0);
        _mapMaterial?.SetShaderParameter("selected_state_tag", 0);
        _borderMaterial?.SetShaderParameter("hovered_province_idx", 0);
        _borderMaterial?.SetShaderParameter("selected_province_idx", 0);
        _overlay?.QueueRedraw();
    }

    public void SetLensMode(MapLensMode lens)
    {
        CurrentLens = lens;
        _mapMaterial.SetShaderParameter("lens_mode", (int)lens);
        if (LatestSnapshot is not null)
            _textures.UploadFullLookup(LatestSnapshot, CurrentLens, _stateById, _nationTagById, _stateTagById, _marketColors);
        _overlay.QueueRedraw();
    }

    public void ApplySnapshot(RenderSnapshot snapshot)
    {
        LatestSnapshot = snapshot;
        IndexSnapshot(snapshot);

        if (_textures.CurrentBaked is null)
        {
            BakeAndBindTextures(snapshot, _textures.CurrentTopology, bypassCache: false);
            RebuildRoadArcCache(snapshot);
            _overlay.RebuildWastelandAndNationLabelCaches(_textures.CurrentBaked, Features, _shortNationNameCache);
        }

        _textures.UploadFullLookup(snapshot, CurrentLens, _stateById, _nationTagById, _stateTagById, _marketColors);
        _dirtyLookupLocations.Clear();
        _overlay.QueueRedraw();
    }

    public void RebuildFromAtlas(EditableWorldAtlas atlas, RenderSnapshot? syntheticSnapshot = null)
    {
        Features = atlas.Features;
        RebuildFeatureCaches();

        var snap = syntheticSnapshot ?? BuildSyntheticSnapshotFromAtlas(atlas);
        LatestSnapshot = snap;
        IndexSnapshot(snap);

        foreach (var n in atlas.Nations)
            _shortNationNameCache[n.Id] = ExtractShortName(n.Name);

        BakeAndBindTextures(snap, atlas.Topology, bypassCache: true);
        RebuildRoadArcCache(snap);
        _overlay.RebuildWastelandAndNationLabelCaches(_textures.CurrentBaked, Features, _shortNationNameCache);
        _textures.UploadFullLookup(snap, CurrentLens, _stateById, _nationTagById, _stateTagById, _marketColors);
        _overlay.QueueRedraw();
    }

    public void MarkProvinceLookupDirty(int locNumericalIndex) => _dirtyLookupLocations.Add(locNumericalIndex);

    public bool TrySelectProvinceById(string provinceId)
    {
        foreach (var kv in _provByNumIdx)
        {
            if (kv.Value.ProvinceId == provinceId)
            {
                SelectLocationInternal(kv.Value);
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
                SelectLocationInternal(kv.Value);
                return true;
            }
        }
        if (fallback is not null)
        {
            SelectLocationInternal(fallback);
            return true;
        }
        return false;
    }

    public Vector2 GetProvinceWorldPosition(string provinceId)
    {
        if (_textures.CurrentBaked is { } baked)
        {
            foreach (var kv in _provByNumIdx)
            {
                if (kv.Value.ProvinceId == provinceId &&
                    baked.ProvinceCentroids.TryGetValue(kv.Key, out var c))
                {
                    return new Vector2(c.X * WorldCanvasSize.X, c.Y * WorldCanvasSize.Y);
                }
            }
        }
        return WorldCanvasSize * 0.5f;
    }

    public bool TryGetStateCenter(string stateId, out Vector2 worldPos)
    {
        if (_textures.CurrentBaked is { } baked && baked.StateCentroids.TryGetValue(stateId, out var c))
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
        var idx = _input.LookupLocationAtWorldPos(_textures.CurrentBaked, worldPos);
        if (idx <= 0 || !_provByNumIdx.TryGetValue(idx, out var loc))
            return false;

        SelectLocationInternal(loc);
        return true;
    }

    public int LookupProvinceIndexAtWorld(Vector2 worldPos) =>
        _input.LookupLocationAtWorldPos(_textures.CurrentBaked, worldPos);

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || _textures.CurrentBaked is null)
            return;

        if (@event is InputEventMouseMotion)
        {
            var worldPos = GetGlobalMousePosition();
            if (EditorInputMode && Input.IsMouseButtonPressed(MouseButton.Left))
                EditorCanvasDragged?.Invoke(worldPos);

            var idx = _input.LookupLocationAtWorldPos(_textures.CurrentBaked, worldPos);
            if (idx != HoveredProvNum)
            {
                HoveredProvNum = idx;
                _mapMaterial.SetShaderParameter("hovered_province_idx", HoveredProvNum);
                _borderMaterial.SetShaderParameter("hovered_province_idx", HoveredProvNum);
                ProvinceHovered?.Invoke(HoveredProvNum > 0 && _provByNumIdx.TryGetValue(HoveredProvNum, out var hp) ? hp : null);
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
                    if (_leftPressPos.DistanceTo(mb.Position) <= 8.0f &&
                        TryClickWorldPosition(GetGlobalMousePosition()))
                    {
                        GetViewport().SetInputAsHandled();
                    }
                }
            }
        }
    }

    private void IndexSnapshot(RenderSnapshot snapshot)
    {
        _provByNumIdx.Clear();
        LocationSpatialGrid.Clear();
        for (var i = 0; i < snapshot.Provinces.Count; i++)
        {
            var p = snapshot.Provinces[i];
            _provByNumIdx[p.NumericalIndex] = p;
            LocationSpatialGrid.Add(p.AnchorX, p.AnchorY, p);
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

        _marketColors.Clear();
        for (var i = 0; i < snapshot.Markets.Count; i++)
        {
            var m = snapshot.Markets[i];
            _marketColors[m.MarketId] = Color.FromHtml(m.ColorHex);
        }

        _shortNationNameCache.Clear();
        _shortNationNameCache[snapshot.Realm.NationId] = ExtractShortName(snapshot.Realm.NationName);
    }

    private void BakeAndBindTextures(RenderSnapshot snapshot, PlanarMapTopology? prebuiltTopology, bool bypassCache)
    {
        var baked = _textures.BakeAndUpload(snapshot.Provinces, Features, prebuiltTopology, _vmapCacheDir, bypassCache);

        _mapRect.Texture = _textures.ProvinceDataTexture;
        _mapMaterial.SetShaderParameter("province_data_tex", _textures.ProvinceDataTexture!);
        _mapMaterial.SetShaderParameter("height_coast_tex", _textures.HeightCoastTexture!);
        _mapMaterial.SetShaderParameter("province_lookup_tex", _textures.LookupTexture!);
        _mapMaterial.SetShaderParameter("camera_zoom", CameraZoom);
        _mapMaterial.SetShaderParameter("lens_mode", (int)CurrentLens);

        _borderMaterial.SetShaderParameter("province_lookup_tex", _textures.LookupTexture!);
        _borderMaterial.SetShaderParameter("camera_zoom", CameraZoom);
        ApplyLayerVisibility();

        _borderMeshNode.Mesh = BorderMeshBuilder.Build(baked, WorldCanvasSize);
    }

    private void SelectLocationInternal(ProvinceSnapshot loc)
    {
        SelectedProvNum = loc.NumericalIndex;
        var stateTag = loc.Kind == LocationKind.SeaZone ? 0 : _stateTagById.GetValueOrDefault(loc.StateId, 0);
        _mapMaterial.SetShaderParameter("selected_province_idx", SelectedProvNum);
        _mapMaterial.SetShaderParameter("selected_state_tag", stateTag);
        _borderMaterial.SetShaderParameter("selected_province_idx", SelectedProvNum);
        _overlay.QueueRedraw();

        ProvinceSelected?.Invoke(loc.ProvinceId, loc.StateId);
        if (!string.IsNullOrEmpty(loc.StateId))
            StateSelected?.Invoke(loc.StateId);
    }

    private void RebuildFeatureCaches()
    {
        CachedRiverPolylines.Clear();
        foreach (var r in Features.Rivers)
        {
            if (r.ControlPoints.Length < 2) continue;
            if (r.ControlPoints.Length >= 4)
            {
                var pts = new Vector2[25];
                var p0 = new Vector2(r.ControlPoints[0].X, r.ControlPoints[0].Y);
                var p1 = new Vector2(r.ControlPoints[1].X, r.ControlPoints[1].Y);
                var p2 = new Vector2(r.ControlPoints[2].X, r.ControlPoints[2].Y);
                var p3 = new Vector2(r.ControlPoints[3].X, r.ControlPoints[3].Y);
                for (var i = 0; i <= 24; i++)
                {
                    var t = i / 24f;
                    var inv = 1f - t;
                    var p = inv * inv * inv * p0 + 3f * inv * inv * t * p1 + 3f * inv * t * t * p2 + t * t * t * p3;
                    pts[i] = new Vector2(p.X * WorldCanvasSize.X, p.Y * WorldCanvasSize.Y);
                }
                CachedRiverPolylines.Add(pts);
            }
            else
            {
                var pts = new Vector2[r.ControlPoints.Length];
                for (var i = 0; i < pts.Length; i++)
                    pts[i] = new Vector2(r.ControlPoints[i].X * WorldCanvasSize.X, r.ControlPoints[i].Y * WorldCanvasSize.Y);
                CachedRiverPolylines.Add(pts);
            }
        }
    }

    private void RebuildRoadArcCache(RenderSnapshot snap)
    {
        CachedRoadArcs.Clear();
        if (_textures.CurrentBaked is not { } baked)
            return;

        var drawnPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var st in snap.States)
        {
            if (!baked.StateCentroids.TryGetValue(st.StateId, out var cA))
                continue;
            var posA = new Vector2(cA.X * WorldCanvasSize.X, cA.Y * WorldCanvasSize.Y);

            foreach (var adjId in st.AdjacentStateIds)
            {
                var key = string.CompareOrdinal(st.StateId, adjId) < 0 ? $"{st.StateId}|{adjId}" : $"{adjId}|{st.StateId}";
                if (!drawnPairs.Add(key) || !baked.StateCentroids.TryGetValue(adjId, out var cB))
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
                CachedRoadArcs.Add(pts);
            }
        }
    }

    private static string ExtractShortName(string full)
    {
        var idx = full.IndexOfAny(['(', '—']);
        var trimmed = idx >= 0 ? full[..idx].Trim() : full.Trim();
        return trimmed.ToUpperInvariant();
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
                st.InitialPops.Sum(p => p.Population),
                50.0,
                0.0,
                "#78909C",
                false,
                st.AdjacentStateIds,
                st.Provinces,
                det.Select(d => d.Id).ToArray(),
                Array.Empty<string>(),
                st.RegionId,
                st.SuperRegionId,
                null));
        }

        var leadNation = atlas.Nations.FirstOrDefault();
        var header = new RealmHeader(
            leadNation?.Id ?? "NONE",
            leadNation?.Name ?? "Editor",
            leadNation?.DynastyName ?? "Casa Soberana",
            leadNation?.RulerName ?? "Regente",
            leadNation?.HeirName ?? "Herdeiro",
            leadNation?.RulerPrimaryAncestry ?? "Humanos",
            leadNation?.EnsureRulerTraits() ?? Array.Empty<string>(),
            100.0, 75.0, 25.0, leadNation?.StartingCourtSpendingPercent ?? 50.0);

        return new RenderSnapshot(
            new GameDate(1380, 1, 1, 0),
            true,
            2,
            header,
            MonetaryAmount.Zero,
            MonetaryAmount.Zero,
            0.25,
            0.0,
            0.0,
            50.0,
            0.0,
            0,
            0,
            0,
            0,
            0,
            new Dictionary<string, double>(),
            Array.Empty<MarketSnapshot>(),
            stateSnaps,
            provSnaps,
            Array.Empty<ProvinceFoodSnapshot>(),
            Array.Empty<EstateSummarySnapshot>(),
            Array.Empty<PopSummarySnapshot>());
    }
}
