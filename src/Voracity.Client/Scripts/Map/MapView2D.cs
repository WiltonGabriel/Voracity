using Godot;
using Voracity.Core.Engine;
using Voracity.Core.Models;

namespace Voracity.Client.Map;

/// <summary>
/// CK3 / Victoria 3 / OpenVic inspired 2D-to-3D cartographic viewport:
/// - Rasterized contiguous provinces & terrain heightmap rendered via Vulkan fragment shader
/// - O(1) pixel-exact province & state picking via PontariaMapRaster
/// - Vector holding hubs (castles, cities, temples, farms, mines, ports) + curved trade roads
/// - Semantic zoom transitions across Nation -> State -> Province/City levels
/// </summary>
public partial class MapView2D : Node2D
{
    public static readonly Vector2 WorldCanvasSize = new(1920, 1120);

    private const int LookupWidth = 512;

    private TextureRect _mapRect = null!;
    private ShaderMaterial _mapMaterial = null!;
    private MapOverlayCanvas _overlay = null!;

    private PontariaMapRaster? _raster;
    private ImageTexture? _provinceDataTex;
    private ImageTexture? _heightCoastTex;
    private Image? _lookupImage;
    private ImageTexture? _lookupTex;

    private RenderSnapshot? _latestSnapshot;
    private readonly Dictionary<int, ProvinceSnapshot> _provByNumIdx = new();
    private readonly Dictionary<string, StateSnapshot> _stateById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nationTagById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _stateTagById = new(StringComparer.Ordinal);

    private MapLensMode _currentLens = MapLensMode.Political;
    private float _cameraZoom = 1.0f;
    private float _elapsedSeconds;
    private int _hoveredProvNum;
    private int _selectedProvNum;
    private int _selectedStateTag;
    private Vector2 _leftPressPos;
    private bool _leftPressActive;

    public event Action<string>? StateSelected;
    public event Action<string, string>? ProvinceSelected;
    public event Action<ProvinceSnapshot?>? ProvinceHovered;

    public MapLensMode CurrentLens => _currentLens;
    public string? SelectedProvinceId => _provByNumIdx.TryGetValue(_selectedProvNum, out var p) ? p.ProvinceId : null;
    public string? SelectedStateId => _provByNumIdx.TryGetValue(_selectedProvNum, out var p) ? p.StateId : null;

    public override void _Ready()
    {
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

        _overlay = new MapOverlayCanvas(this);
        AddChild(_overlay);
    }

    public override void _Process(double delta)
    {
        _elapsedSeconds += (float)delta;
        if (_raster is not null)
            _mapMaterial.SetShaderParameter("time_seconds", _elapsedSeconds);
    }

    public void SetCameraZoom(float zoom)
    {
        _cameraZoom = Mathf.Clamp(zoom, 0.55f, 3.8f);
        _mapMaterial.SetShaderParameter("camera_zoom", _cameraZoom);
        _overlay.QueueRedraw();
    }

    public void ResetMap()
    {
        _hoveredProvNum = 0;
        _selectedProvNum = 0;
        _selectedStateTag = 0;
        _mapMaterial?.SetShaderParameter("hovered_province_idx", 0);
        _mapMaterial?.SetShaderParameter("selected_province_idx", 0);
        _mapMaterial?.SetShaderParameter("selected_state_tag", 0);
        _overlay?.QueueRedraw();
    }

    public void SetLensMode(MapLensMode lens)
    {
        _currentLens = lens;
        _mapMaterial.SetShaderParameter("is_political_lens", lens == MapLensMode.Political);
        if (_latestSnapshot is not null)
            UpdateLookupTexture(_latestSnapshot);
        _overlay.QueueRedraw();
    }

    public void ApplySnapshot(RenderSnapshot snapshot)
    {
        _latestSnapshot = snapshot;

        _provByNumIdx.Clear();
        foreach (var p in snapshot.Provinces)
            _provByNumIdx[p.NumericalIndex] = p;

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

        if (_raster is null)
            BuildStaticMapTextures(snapshot);

        UpdateLookupTexture(snapshot);
        _overlay.QueueRedraw();
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
        if (_raster is not null)
        {
            foreach (var kv in _provByNumIdx)
            {
                if (kv.Value.ProvinceId == provinceId &&
                    _raster.ProvinceCentroids.TryGetValue(kv.Key, out var c))
                {
                    return new Vector2(c.X * WorldCanvasSize.X, c.Y * WorldCanvasSize.Y);
                }
            }
        }
        return WorldCanvasSize * 0.5f;
    }

    public bool TryGetStateCenter(string stateId, out Vector2 worldPos)
    {
        if (_raster is not null && _raster.StateCentroids.TryGetValue(stateId, out var c))
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

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || _raster is null)
            return;

        if (@event is InputEventMouseMotion)
        {
            var worldPos = GetGlobalMousePosition();
            var idx = LookupProvinceAtWorldPos(worldPos);
            if (idx != _hoveredProvNum)
            {
                _hoveredProvNum = idx;
                _mapMaterial.SetShaderParameter("hovered_province_idx", _hoveredProvNum);
                ProvinceHovered?.Invoke(_hoveredProvNum > 0 && _provByNumIdx.TryGetValue(_hoveredProvNum, out var hp) ? hp : null);
                _overlay.QueueRedraw();
            }
            return;
        }

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
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

    private int LookupProvinceAtWorldPos(Vector2 worldPos)
    {
        if (_raster is null)
            return 0;

        var nx = worldPos.X / WorldCanvasSize.X;
        var ny = worldPos.Y / WorldCanvasSize.Y;
        return _raster.LookupProvinceIndex(nx, ny);
    }

    private void SelectProvinceInternal(ProvinceSnapshot prov)
    {
        _selectedProvNum = prov.NumericalIndex;
        _selectedStateTag = _stateTagById.GetValueOrDefault(prov.StateId, 0);
        _mapMaterial.SetShaderParameter("selected_province_idx", _selectedProvNum);
        _mapMaterial.SetShaderParameter("selected_state_tag", _selectedStateTag);
        _overlay.QueueRedraw();

        ProvinceSelected?.Invoke(prov.ProvinceId, prov.StateId);
        StateSelected?.Invoke(prov.StateId);
    }

    private void BuildStaticMapTextures(RenderSnapshot snapshot)
    {
        _raster = PontariaMapGenerator.Generate(snapshot.Provinces);

        var pImg = Image.CreateFromData(_raster.Width, _raster.Height, false, Image.Format.Rgb8, _raster.ProvinceDataRgb);
        _provinceDataTex = ImageTexture.CreateFromImage(pImg);

        var hImg = Image.CreateFromData(_raster.Width, _raster.Height, false, Image.Format.Rgb8, _raster.HeightCoastRgb);
        _heightCoastTex = ImageTexture.CreateFromImage(hImg);

        _lookupImage = Image.CreateEmpty(LookupWidth, 2, false, Image.Format.Rgba8);
        _lookupTex = ImageTexture.CreateFromImage(_lookupImage);

        _mapRect.Texture = _provinceDataTex;
        _mapMaterial.SetShaderParameter("province_data_tex", _provinceDataTex);
        _mapMaterial.SetShaderParameter("height_coast_tex", _heightCoastTex);
        _mapMaterial.SetShaderParameter("province_lookup_tex", _lookupTex);
        _mapMaterial.SetShaderParameter("camera_zoom", _cameraZoom);
        _mapMaterial.SetShaderParameter("is_political_lens", _currentLens == MapLensMode.Political);
    }

    private void UpdateLookupTexture(RenderSnapshot snapshot)
    {
        if (_lookupImage is null || _lookupTex is null)
            return;

        _lookupImage.SetPixel(0, 0, new Color(0.08f, 0.13f, 0.19f, 1.0f));
        _lookupImage.SetPixel(0, 1, new Color(0f, 0f, 0f, 0f));

        foreach (var prov in snapshot.Provinces)
        {
            if (prov.NumericalIndex <= 0 || prov.NumericalIndex >= LookupWidth)
                continue;

            var state = _stateById.GetValueOrDefault(prov.StateId);
            var lensCol = ResolveLensColor(prov, state);

            var nationTag = _nationTagById.GetValueOrDefault(prov.OwnerNationId, 1);
            var stateTag = _stateTagById.GetValueOrDefault(prov.StateId, 1);
            var occupiedFlag = prov.OccupiedByNationId is not null ? 1.0f : 0.0f;
            var occupierHue = prov.OccupiedByNationId is not null && _nationTagById.TryGetValue(prov.OccupiedByNationId, out var occTag)
                ? (occTag % 16) / 15.0f
                : 0.0f;

            _lookupImage.SetPixel(prov.NumericalIndex, 0, lensCol);
            _lookupImage.SetPixel(prov.NumericalIndex, 1, new Color(
                (nationTag * 11) / 255.0f,
                (stateTag * 9) / 255.0f,
                occupiedFlag,
                occupierHue));
        }

        _lookupTex.Update(_lookupImage);
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

        // Slight organic lightness variation per province so sibling provinces within a state feel subtly modeled
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

    /// <summary>
    /// Batched vector canvas rendering trade routes, curved inter-city roads, holding architecture icons,
    /// fortifications, and zoom-adaptive typography cleanly on top of the terrain/border shader.
    /// </summary>
    private sealed partial class MapOverlayCanvas : Node2D
    {
        private readonly MapView2D _owner;

        public MapOverlayCanvas(MapView2D owner)
        {
            _owner = owner;
        }

        public override void _Draw()
        {
            var raster = _owner._raster;
            var snap = _owner._latestSnapshot;
            if (raster is null || snap is null)
                return;

            var zoom = _owner._cameraZoom;
            var font = ThemeDB.Singleton.FallbackFont;
            var invScale = Mathf.Clamp(1.0f / Mathf.Sqrt(zoom), 0.45f, 1.30f);

            DrawContinentalRivers(invScale, zoom);
            DrawStateRoadsAndMaritimeLanes(raster, snap, invScale, zoom);
            DrawProvinceHoldings(raster, snap, font, invScale, zoom);
            DrawHierarchyLabels(raster, snap, font, invScale, zoom);
        }

        private void DrawContinentalRivers(float invScale, float zoom)
        {
            // Canonical Pontarian river systems flowing from highland ridges toward gulfs & bays
            Vector2[][] rivers =
            [
                [new(0.24f, 0.27f), new(0.20f, 0.34f), new(0.14f, 0.37f), new(0.08f, 0.38f)], // Rio Brigg-Valen
                [new(0.42f, 0.24f), new(0.46f, 0.31f), new(0.51f, 0.36f), new(0.57f, 0.34f)], // Rio Claro de Dalingian-Hasta
                [new(0.61f, 0.36f), new(0.66f, 0.43f), new(0.73f, 0.48f), new(0.84f, 0.52f)], // Grande Rio Oriental (Hasta-Khan)
                [new(0.37f, 0.49f), new(0.31f, 0.56f), new(0.24f, 0.62f), new(0.15f, 0.67f)], // Rio Ferro-Levante
                [new(0.49f, 0.57f), new(0.52f, 0.67f), new(0.56f, 0.77f), new(0.61f, 0.86f)], // Rio Rubro de Varakrima
                [new(0.68f, 0.24f), new(0.75f, 0.28f), new(0.82f, 0.31f), new(0.89f, 0.30f)]  // Rio Sombrio Élfico
            ];

            var riverColor = new Color(0.16f, 0.34f, 0.48f, zoom >= 1.2f ? 0.78f : 0.55f);
            var bankHighlight = new Color(0.08f, 0.14f, 0.20f, 0.35f);

            foreach (var r in rivers)
            {
                var pts = new Vector2[25];
                for (var i = 0; i <= 24; i++)
                {
                    var t = i / 24f;
                    var p = EvalCatmull(r[0], r[1], r[2], r[3], t);
                    pts[i] = new Vector2(p.X * WorldCanvasSize.X, p.Y * WorldCanvasSize.Y);
                }
                DrawPolyline(pts, bankHighlight, (3.2f * invScale), antialiased: true);
                DrawPolyline(pts, riverColor, (1.8f * invScale), antialiased: true);
            }
        }

        private static Vector2 EvalCatmull(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            var inv = 1f - t;
            return inv * inv * inv * p0
                 + 3f * inv * inv * t * p1
                 + 3f * inv * t * t * p2
                 + t * t * t * p3;
        }

        private void DrawStateRoadsAndMaritimeLanes(PontariaMapRaster raster, RenderSnapshot snap, float invScale, float zoom)
        {
            var drawnPairs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var st in snap.States)
            {
                if (!raster.StateCentroids.TryGetValue(st.StateId, out var cA))
                    continue;
                var posA = new Vector2(cA.X * WorldCanvasSize.X, cA.Y * WorldCanvasSize.Y);

                foreach (var adjId in st.AdjacentStateIds)
                {
                    var key = string.CompareOrdinal(st.StateId, adjId) < 0 ? $"{st.StateId}|{adjId}" : $"{adjId}|{st.StateId}";
                    if (!drawnPairs.Add(key) || !raster.StateCentroids.TryGetValue(adjId, out var cB))
                        continue;

                    var posB = new Vector2(cB.X * WorldCanvasSize.X, cB.Y * WorldCanvasSize.Y);
                    var mid = (posA + posB) * 0.5f;
                    var normal = (posB - posA).Orthogonal().Normalized();
                    var ctrl = mid + normal * (posA.DistanceTo(posB) * 0.12f);

                    var isTradeOrWar = st.IsAtWarWithPlayer || (_owner._stateById.TryGetValue(adjId, out var adjSt) && (adjSt.IsAtWarWithPlayer || (st.HasTradePactWithPlayer && adjSt.OwnerNationId != st.OwnerNationId)));
                    var lineCol = st.IsAtWarWithPlayer
                        ? new Color(0.85f, 0.24f, 0.18f, 0.45f)
                        : isTradeOrWar
                            ? new Color(0.90f, 0.72f, 0.30f, 0.42f)
                            : new Color(0.24f, 0.19f, 0.13f, zoom >= 1.0f ? 0.34f : 0.20f);

                    DrawQuadraticArc(posA, ctrl, posB, lineCol, 1.6f * invScale, 12);
                }
            }
        }

        private void DrawProvinceHoldings(PontariaMapRaster raster, RenderSnapshot snap, Font font, float invScale, float zoom)
        {
            foreach (var prov in snap.Provinces)
            {
                if (!raster.ProvinceCentroids.TryGetValue(prov.NumericalIndex, out var centroid))
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

                // Shielded emblem backdrop + distinct holding silhouette
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

                // Fort bastion badge if fortified
                if (prov.FortLevel > 0)
                {
                    var fPos = pos + new Vector2(s * 0.95f, -s * 0.85f);
                    DrawRect(new Rect2(fPos - new Vector2(s * 0.38f, s * 0.38f), new Vector2(s * 0.76f, s * 0.76f)), new Color(0.20f, 0.22f, 0.26f, alpha));
                    DrawRect(new Rect2(fPos - new Vector2(s * 0.38f, s * 0.38f), new Vector2(s * 0.76f, s * 0.76f)), new Color(0.90f, 0.76f, 0.42f, alpha), false, 1.0f * invScale);
                }

                // Province / City label appears cleanly at mid/high zoom or when hovered/selected (avoids strategic-zoom clash with Nation names)
                if (zoom >= 1.12f || isSelected || isHovered)
                {
                    var labelAlpha = isSelected || isHovered
                        ? 1.0f
                        : Mathf.Clamp((zoom - 1.12f) / 0.34f, 0f, 1f);
                    var fontSize = Mathf.Clamp((int)(12.5f * invScale), 9, 17);
                    var tierTag = prov.HoldingLevel > 1 ? $" [{prov.HoldingLevel}]" : string.Empty;
                    var text = $"{prov.Name}{tierTag}";
                    var textOffset = new Vector2(-font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X * 0.5f, s * 2.35f + fontSize * 0.7f);
                    var textCol = isSelected
                        ? new Color(1.0f, 0.93f, 0.62f, labelAlpha)
                        : new Color(0.94f, 0.92f, 0.86f, labelAlpha * 0.94f);
                    DrawStringOutline(font, pos + textOffset, text, HorizontalAlignment.Left, -1, fontSize, Mathf.Max(2, (int)(3 * invScale)), new Color(0.04f, 0.04f, 0.05f, 0.85f * labelAlpha));
                    DrawString(font, pos + textOffset, text, HorizontalAlignment.Left, -1, fontSize, textCol);
                }
            }
        }

        private void DrawHierarchyLabels(PontariaMapRaster raster, RenderSnapshot snap, Font font, float invScale, float zoom)
        {
            // Nation names shine when zoomed out or mid-range, smoothly fading as user zooms deep into province detail
            var nationAlpha = 1.0f - Mathf.Clamp((zoom - 1.35f) / 0.65f, 0f, 0.88f);
            if (nationAlpha > 0.08f)
            {
                var nationNameMap = snap.PlayerDiplomacy.ToDictionary(d => d.TargetNationId, d => d.TargetNationName, StringComparer.Ordinal);
                nationNameMap[snap.PlayerNationId] = snap.PlayerNationName;

                foreach (var (nationId, anchor) in raster.NationLabelAnchors)
                {
                    if (!nationNameMap.TryGetValue(nationId, out var name))
                        continue;

                    var shortName = name.Split(['(', '—'], StringSplitOptions.TrimEntries)[0].ToUpperInvariant();
                    var pos = new Vector2(anchor.X * WorldCanvasSize.X, anchor.Y * WorldCanvasSize.Y - 12f * invScale);
                    var fontSize = Mathf.Clamp((int)((16f + anchor.SpanWidth * 48f) * invScale), 13, 30);
                    var sz = font.GetStringSize(shortName, HorizontalAlignment.Left, -1, fontSize);
                    var origin = pos - new Vector2(sz.X * 0.5f, 0f);

                    DrawStringOutline(font, origin, shortName, HorizontalAlignment.Left, -1, fontSize, Mathf.Max(3, (int)(5 * invScale)), new Color(0.05f, 0.04f, 0.03f, 0.82f * nationAlpha));
                    DrawString(font, origin, shortName, HorizontalAlignment.Left, -1, fontSize, new Color(0.98f, 0.94f, 0.82f, 0.94f * nationAlpha));
                }
            }

            // State / Region names appear softly at mid-zoom so multi-state realms show both Kingdom and Duchy/State structure
            if (zoom >= 0.96f && zoom <= 2.45f)
            {
                var stAlpha = Mathf.Clamp((zoom - 0.96f) / 0.35f, 0f, 1f) * (1.0f - Mathf.Clamp((zoom - 1.95f) / 0.50f, 0f, 0.75f));
                foreach (var st in snap.States)
                {
                    if (!raster.StateCentroids.TryGetValue(st.StateId, out var centroid))
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
                    // Tower keep + battlements
                    DrawRect(new Rect2(center - new Vector2(r * 0.65f, r * 0.55f), new Vector2(r * 1.30f, r * 1.15f)), color);
                    DrawRect(new Rect2(center - new Vector2(r * 0.80f, r * 0.85f), new Vector2(r * 0.42f, r * 0.40f)), color);
                    DrawRect(new Rect2(center + new Vector2(r * 0.38f, -r * 0.85f), new Vector2(r * 0.42f, r * 0.40f)), color);
                    break;
                case "city":
                    // Stepped citadel / townhouses; capitals gain golden crown diamond above
                    DrawRect(new Rect2(center - new Vector2(r * 0.85f, r * 0.15f), new Vector2(r * 0.55f, r * 0.75f)), color);
                    DrawRect(new Rect2(center - new Vector2(r * 0.25f, r * 0.75f), new Vector2(r * 0.58f, r * 1.35f)), color);
                    DrawRect(new Rect2(center + new Vector2(r * 0.36f, -r * 0.25f), new Vector2(r * 0.50f, r * 0.85f)), color);
                    break;
                case "temple":
                    // Cathedral spire triangle + base
                    DrawColoredPolygon(
                    [
                        center + new Vector2(0f, -r * 1.0f),
                        center + new Vector2(r * 0.75f, r * 0.65f),
                        center + new Vector2(-r * 0.75f, r * 0.65f)
                    ], color);
                    break;
                case "mine":
                    // Rotated ore gem / shaft diamond
                    DrawColoredPolygon(
                    [
                        center + new Vector2(0f, -r * 0.90f),
                        center + new Vector2(r * 0.85f, 0f),
                        center + new Vector2(0f, r * 0.90f),
                        center + new Vector2(-r * 0.85f, 0f)
                    ], color);
                    break;
                case "port":
                    // Harbour mast + hull wedge
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
                    // Conifer crown triangle
                    DrawColoredPolygon(
                    [
                        center + new Vector2(0f, -r * 0.95f),
                        center + new Vector2(r * 0.80f, r * 0.55f),
                        center + new Vector2(-r * 0.80f, r * 0.55f)
                    ], color);
                    break;
                default: // farm & plains hamlet
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

        private void DrawQuadraticArc(Vector2 a, Vector2 ctrl, Vector2 b, Color color, float width, int steps)
        {
            var pts = new Vector2[steps + 1];
            for (var i = 0; i <= steps; i++)
            {
                var t = i / (float)steps;
                var inv = 1f - t;
                pts[i] = inv * inv * a + 2f * inv * t * ctrl + t * t * b;
            }
            DrawPolyline(pts, color, width, antialiased: true);
        }
    }
}
