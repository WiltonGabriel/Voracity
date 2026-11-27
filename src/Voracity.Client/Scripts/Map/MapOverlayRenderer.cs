using Godot;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Client.Map;

/// <summary>
/// Zero-GC cartographic overlay renderer for Godot 4:
/// pre-allocates spline curves, barrier labels, nation label sort order, and bounding-box collision buffers
/// so _Draw() executes with zero List/LINQ heap allocations per frame.
/// </summary>
public sealed partial class MapOverlayRenderer : Node2D
{
    private readonly MapView2D _owner;

    // Pre-allocated scratch buffers (reused across frames -> ZERO GC inside _Draw)
    private readonly List<Rect2> _placedRectsScratch = new(32);
    private readonly List<(string Id, string Text, float X, float Y, float SpanWidth)> _sortedNationLabels = new(24);
    private readonly List<(string Name, float X, float Y)> _wastelandLabels = new(4);
    private readonly Vector2[] _chevronPtsScratch = new Vector2[3];

    public MapOverlayRenderer(MapView2D owner)
    {
        _owner = owner;
    }

    public void RebuildWastelandAndNationLabelCaches(
        BakedMapArtifact? baked,
        GeographyFeaturesData features,
        IReadOnlyDictionary<string, string> shortNationNames)
    {
        _sortedNationLabels.Clear();
        if (baked is not null)
        {
            foreach (var kv in baked.NationLabelAnchors)
            {
                if (kv.Key == "NONE" || !shortNationNames.TryGetValue(kv.Key, out var shortName))
                    continue;
                _sortedNationLabels.Add((kv.Key, shortName, kv.Value.X, kv.Value.Y, kv.Value.SpanWidth));
            }
            _sortedNationLabels.Sort((a, b) => b.SpanWidth.CompareTo(a.SpanWidth));
        }

        _wastelandLabels.Clear();
        for (var i = 0; i < features.Landmasses.Length; i++)
        {
            var lm = features.Landmasses[i];
            if (!string.Equals(lm.Kind, "wasteland_continent", StringComparison.Ordinal) || lm.PolygonPoints.Length == 0)
                continue;

            float sx = 0f, sy = 0f;
            for (var p = 0; p < lm.PolygonPoints.Length; p++)
            {
                sx += lm.PolygonPoints[p].X;
                sy += lm.PolygonPoints[p].Y;
            }
            _wastelandLabels.Add((lm.Name.ToUpperInvariant(), sx / lm.PolygonPoints.Length, sy / lm.PolygonPoints.Length));
        }
    }

    public override void _Draw()
    {
        var baked = _owner.CurrentBaked;
        var snap = _owner.LatestSnapshot;
        if (baked is null || snap is null)
            return;

        var zoom = _owner.CameraZoom;
        var font = ThemeDB.Singleton.FallbackFont;
        var invScale = Mathf.Clamp(1.0f / Mathf.Sqrt(zoom), 0.45f, 1.30f);

        var canvasInv = GetCanvasTransform().AffineInverse();
        var vpSize = GetViewportRect().Size;
        var topLeft = (canvasInv * Vector2.Zero) / MapView2D.WorldCanvasSize;
        var botRight = (canvasInv * vpSize) / MapView2D.WorldCanvasSize;
        var minX = Math.Clamp(Math.Min(topLeft.X, botRight.X) - 0.06f, 0f, 1f);
        var minY = Math.Clamp(Math.Min(topLeft.Y, botRight.Y) - 0.06f, 0f, 1f);
        var maxX = Math.Clamp(Math.Max(topLeft.X, botRight.X) + 0.06f, 0f, 1f);
        var maxY = Math.Clamp(Math.Max(topLeft.Y, botRight.Y) + 0.06f, 0f, 1f);

        if (_owner.Layers.ShowRivers)
            DrawCachedRivers(invScale, zoom);

        DrawDataDrivenBarriersAndMassifs(font, invScale, zoom);

        if (_owner.Layers.ShowRoads)
            DrawCachedLocationRoads(invScale, zoom);

        if (_owner.Layers.ShowHoldings)
            DrawCulledLocationNodes(baked, font, invScale, zoom, minX, minY, maxX, maxY);

        if (_owner.Layers.ShowLabels)
            DrawHierarchyLabels(baked, snap, font, invScale, zoom);
    }

    private void DrawCachedRivers(float invScale, float zoom)
    {
        var riverColor = new Color(0.16f, 0.34f, 0.48f, zoom >= 1.2f ? 0.78f : 0.55f);
        var bankHighlight = new Color(0.08f, 0.14f, 0.20f, 0.35f);
        var list = _owner.CachedRiverPolylines;
        for (var i = 0; i < list.Count; i++)
        {
            var pts = list[i];
            DrawPolyline(pts, bankHighlight, 3.2f * invScale, antialiased: true);
            DrawPolyline(pts, riverColor, 1.8f * invScale, antialiased: true);
        }
    }

    private void DrawDataDrivenBarriersAndMassifs(Font font, float invScale, float zoom)
    {
        var ranges = _owner.Features.MountainRanges;
        var ridgeInk = new Color(0.18f, 0.15f, 0.16f, 0.72f);
        var ridgeSnow = new Color(0.72f, 0.68f, 0.65f, 0.45f);

        for (var m = 0; m < ranges.Length; m++)
        {
            var mr = ranges[m];
            if (!mr.IsImpassable || mr.SpinePoints.Length < 2)
                continue;

            for (var s = 0; s < mr.SpinePoints.Length - 1; s++)
            {
                var a = new Vector2(mr.SpinePoints[s].X * MapView2D.WorldCanvasSize.X, mr.SpinePoints[s].Y * MapView2D.WorldCanvasSize.Y);
                var b = new Vector2(mr.SpinePoints[s + 1].X * MapView2D.WorldCanvasSize.X, mr.SpinePoints[s + 1].Y * MapView2D.WorldCanvasSize.Y);
                for (var k = 0; k < 3; k++)
                {
                    var p = a.Lerp(b, (k + 0.5f) / 3f);
                    var r = 6.5f * invScale;
                    _chevronPtsScratch[0] = p + new Vector2(-r, r * 0.6f);
                    _chevronPtsScratch[1] = p + new Vector2(0f, -r * 0.85f);
                    _chevronPtsScratch[2] = p + new Vector2(r, r * 0.6f);
                    DrawPolyline(_chevronPtsScratch, ridgeInk, 2.2f * invScale, antialiased: true);

                    _chevronPtsScratch[0] = p + new Vector2(-r * 0.45f, -r * 0.1f);
                    _chevronPtsScratch[1] = p + new Vector2(0f, -r * 0.85f);
                    _chevronPtsScratch[2] = p + new Vector2(r * 0.45f, -r * 0.1f);
                    DrawPolyline(_chevronPtsScratch, ridgeSnow, 1.3f * invScale, antialiased: true);
                }
            }
        }

        // Data-driven cartographic barriers (from geography_features.json barriers[])
        var barriers = _owner.Features.EnsureBarriers();
        var labelAlpha = zoom >= 0.72f ? Mathf.Clamp((zoom - 0.72f) / 0.30f, 0f, 1f) : 0f;
        var wallW = Mathf.Max(3.8f, 7.6f * invScale);

        for (var b = 0; b < barriers.Length; b++)
        {
            var bar = barriers[b];
            if (bar.PolylinePoints.Length < 2)
                continue;

            for (var s = 0; s < bar.PolylinePoints.Length - 1; s++)
            {
                var wallTop = new Vector2(bar.PolylinePoints[s].X * MapView2D.WorldCanvasSize.X, bar.PolylinePoints[s].Y * MapView2D.WorldCanvasSize.Y);
                var wallBot = new Vector2(bar.PolylinePoints[s + 1].X * MapView2D.WorldCanvasSize.X, bar.PolylinePoints[s + 1].Y * MapView2D.WorldCanvasSize.Y);

                DrawLine(wallTop + new Vector2(2.5f * invScale, 1.5f * invScale), wallBot + new Vector2(2.5f * invScale, 1.5f * invScale),
                    new Color(0.04f, 0.03f, 0.04f, 0.85f), wallW * 1.45f, antialiased: true);
                DrawLine(wallTop, wallBot, new Color(0.28f, 0.25f, 0.29f, 0.98f), wallW, antialiased: true);
                DrawLine(wallTop + new Vector2(-wallW * 0.18f, 0f), wallBot + new Vector2(-wallW * 0.18f, 0f),
                    new Color(0.62f, 0.58f, 0.54f, 0.92f), wallW * 0.35f, antialiased: true);

                for (var t = 0; t < 5; t++)
                {
                    var pt = wallTop.Lerp(wallBot, t / 4f);
                    var towerR = wallW * 0.68f;
                    DrawRect(new Rect2(pt - new Vector2(towerR, towerR), new Vector2(towerR * 2f, towerR * 2f)),
                        new Color(0.18f, 0.16f, 0.19f, 1.0f));
                    DrawRect(new Rect2(pt - new Vector2(towerR * 0.55f, towerR * 0.55f), new Vector2(towerR * 1.1f, towerR * 1.1f)),
                        t == 2 ? new Color(0.95f, 0.62f, 0.22f, 0.95f) : new Color(0.75f, 0.68f, 0.55f, 0.85f));
                }
            }

            if (_owner.Layers.ShowLabels && labelAlpha > 0.05f)
            {
                var p0 = new Vector2(bar.PolylinePoints[0].X * MapView2D.WorldCanvasSize.X, bar.PolylinePoints[0].Y * MapView2D.WorldCanvasSize.Y);
                var bSize = Mathf.Clamp((int)(11f * invScale), 9, 15);
                var bPos = p0 + new Vector2(-106f * invScale, -10f * invScale);
                var title = bar.Name.ToUpperInvariant();
                DrawStringOutline(font, bPos, title, HorizontalAlignment.Left, -1, bSize,
                    Mathf.Max(2, (int)(3 * invScale)), new Color(0.05f, 0.04f, 0.04f, 0.90f * labelAlpha));
                DrawString(font, bPos, title, HorizontalAlignment.Left, -1, bSize,
                    new Color(0.95f, 0.80f, 0.48f, 0.95f * labelAlpha));
            }
        }

        if (_owner.Layers.ShowLabels && labelAlpha > 0.05f)
        {
            var vSize = Mathf.Clamp((int)(12f * invScale), 9, 16);
            for (var i = 0; i < _wastelandLabels.Count; i++)
            {
                var wl = _wastelandLabels[i];
                var vPos = new Vector2(wl.X * MapView2D.WorldCanvasSize.X, wl.Y * MapView2D.WorldCanvasSize.Y);
                DrawStringOutline(font, vPos, wl.Name, HorizontalAlignment.Left, -1, vSize,
                    Mathf.Max(2, (int)(3 * invScale)), new Color(0.03f, 0.03f, 0.04f, 0.92f * labelAlpha));
                DrawString(font, vPos, wl.Name, HorizontalAlignment.Left, -1, vSize,
                    new Color(0.84f, 0.87f, 0.94f, 0.92f * labelAlpha));
            }
        }
    }

    private void DrawCachedLocationRoads(float invScale, float zoom)
    {
        if (zoom < 1.05f)
            return;

        var arcs = _owner.CachedRoadArcs;
        var alphaBoost = Mathf.Clamp((zoom - 1.05f) / 0.35f, 0f, 1f);
        var roadCol = new Color(0.24f, 0.19f, 0.13f, 0.30f * alphaBoost);
        for (var i = 0; i < arcs.Count; i++)
            DrawPolyline(arcs[i], roadCol, 1.5f * invScale, antialiased: true);
    }

    private void DrawCulledLocationNodes(
        BakedMapArtifact baked,
        Font font,
        float invScale,
        float zoom,
        float minX,
        float minY,
        float maxX,
        float maxY)
    {
        var visible = _owner.VisibleLocationScratch;
        _owner.LocationSpatialGrid.QueryRect(minX, minY, maxX, maxY, visible);

        for (var i = 0; i < visible.Count; i++)
        {
            var loc = visible[i];
            if (loc.Kind == LocationKind.Impassable)
                continue;
            if (!baked.ProvinceCentroids.TryGetValue(loc.NumericalIndex, out var centroid))
                continue;

            var isSelected = loc.NumericalIndex == _owner.SelectedProvNum;
            var isHovered = loc.NumericalIndex == _owner.HoveredProvNum;

            if (loc.Kind == LocationKind.SeaZone)
            {
                if (isSelected || isHovered || zoom >= 1.35f)
                {
                    var spos = new Vector2(centroid.X * MapView2D.WorldCanvasSize.X, centroid.Y * MapView2D.WorldCanvasSize.Y);
                    if (isSelected)
                        DrawArc(spos, 10f * invScale, 0f, Mathf.Tau, 20, new Color(0.45f, 0.84f, 0.96f, 0.92f), 1.6f * invScale);
                    else if (isHovered)
                        DrawArc(spos, 8f * invScale, 0f, Mathf.Tau, 18, new Color(0.75f, 0.92f, 0.98f, 0.78f), 1.3f * invScale);

                    if (zoom >= 1.35f || isSelected || isHovered)
                    {
                        var fontSize = Mathf.Clamp((int)(10.5f * invScale), 8, 14);
                        var tw = font.GetStringSize(loc.Name, HorizontalAlignment.Left, -1, fontSize).X;
                        var tp = spos + new Vector2(-tw * 0.5f, 12f * invScale);
                        DrawStringOutline(font, tp, loc.Name, HorizontalAlignment.Left, -1, fontSize, 2, new Color(0.03f, 0.06f, 0.09f, 0.80f));
                        DrawString(font, tp, loc.Name, HorizontalAlignment.Left, -1, fontSize, new Color(0.68f, 0.84f, 0.94f, 0.85f));
                    }
                }
                continue;
            }

            var isMajor = loc.IsCapital || loc.IsMarketCenter || loc.Rank >= LocationRank.Town;
            if (!isMajor && !isSelected && !isHovered && zoom < 0.90f)
                continue;

            var alpha = isMajor || isSelected || isHovered
                ? 1.0f
                : Mathf.Clamp((zoom - 0.88f) / 0.38f, 0f, 1f);

            var pos = new Vector2(centroid.X * MapView2D.WorldCanvasSize.X, centroid.Y * MapView2D.WorldCanvasSize.Y);
            var rankBonus = (int)loc.Rank * 0.85f;
            var s = (loc.IsCapital ? 7.4f : 5.2f + rankBonus) * invScale;

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

            var iconColor = loc.Kind == LocationKind.Corridor
                ? new Color(0.90f, 0.78f, 0.44f, alpha)
                : loc.Rank switch
                {
                    LocationRank.Megalopolis => new Color(1.0f, 0.88f, 0.36f, alpha),
                    LocationRank.City => new Color(0.95f, 0.81f, 0.36f, alpha),
                    LocationRank.Town => new Color(0.84f, 0.86f, 0.76f, alpha),
                    _ => new Color(0.76f, 0.72f, 0.60f, alpha)
                };

            DrawCircle(pos, s * 0.55f, iconColor);
            DrawArc(pos, s * 1.10f, 0f, Mathf.Tau, 18, loc.IsCapital
                ? new Color(0.98f, 0.82f, 0.32f, 0.95f * alpha)
                : loc.IsMarketCenter
                    ? new Color(0.42f, 0.84f, 0.94f, 0.92f * alpha)
                    : new Color(0.55f, 0.50f, 0.40f, 0.78f * alpha), 1.2f * invScale);

            if (zoom >= 1.12f || isSelected || isHovered)
            {
                var labelAlpha = isSelected || isHovered ? 1.0f : Mathf.Clamp((zoom - 1.12f) / 0.34f, 0f, 1f);
                var fontSize = Mathf.Clamp((int)(12.5f * invScale), 9, 17);
                var label = loc.DynamicDisplayName ?? loc.Name;
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
            _placedRectsScratch.Clear();
            for (var i = 0; i < _sortedNationLabels.Count; i++)
            {
                var nl = _sortedNationLabels[i];
                var basePos = new Vector2(nl.X * MapView2D.WorldCanvasSize.X, nl.Y * MapView2D.WorldCanvasSize.Y - 10f * invScale);
                var fontSize = Mathf.Clamp((int)((11f + nl.SpanWidth * 40f) * invScale), 10, 24);
                var sz = font.GetStringSize(nl.Text, HorizontalAlignment.Left, -1, fontSize);
                var origin = basePos - new Vector2(sz.X * 0.5f, 0f);

                for (var step = 0; step < 5; step++)
                {
                    var rect = new Rect2(origin - new Vector2(0, sz.Y * 0.85f), sz);
                    if (!IntersectsAnyPlaced(rect))
                        break;
                    var dir = (step % 2 == 0 ? 1 : -1) * ((step / 2) + 1);
                    origin.Y = basePos.Y + dir * (sz.Y + 3f);
                }

                _placedRectsScratch.Add(new Rect2(origin - new Vector2(0, sz.Y * 0.85f), sz));
                DrawStringOutline(font, origin, nl.Text, HorizontalAlignment.Left, -1, fontSize, Mathf.Max(2, (int)(4 * invScale)), new Color(0.05f, 0.04f, 0.03f, 0.85f * nationAlpha));
                DrawString(font, origin, nl.Text, HorizontalAlignment.Left, -1, fontSize, new Color(0.98f, 0.94f, 0.82f, 0.94f * nationAlpha));
            }
        }

        if (zoom >= 0.96f && zoom <= 2.45f)
        {
            var stAlpha = Mathf.Clamp((zoom - 0.96f) / 0.35f, 0f, 1f) * (1.0f - Mathf.Clamp((zoom - 1.95f) / 0.50f, 0f, 0.75f));
            for (var i = 0; i < snap.States.Count; i++)
            {
                var st = snap.States[i];
                if (!baked.StateCentroids.TryGetValue(st.StateId, out var centroid))
                    continue;

                var pos = new Vector2(centroid.X * MapView2D.WorldCanvasSize.X, centroid.Y * MapView2D.WorldCanvasSize.Y - 18f * invScale);
                var fontSize = Mathf.Clamp((int)(13f * invScale), 10, 17);
                var sz = font.GetStringSize(st.Name, HorizontalAlignment.Left, -1, fontSize);
                var origin = pos - new Vector2(sz.X * 0.5f, 0f);

                DrawStringOutline(font, origin, st.Name, HorizontalAlignment.Left, -1, fontSize, Mathf.Max(2, (int)(3 * invScale)), new Color(0.04f, 0.04f, 0.05f, 0.78f * stAlpha));
                DrawString(font, origin, st.Name, HorizontalAlignment.Left, -1, fontSize, new Color(0.84f, 0.89f, 0.95f, 0.88f * stAlpha));
            }
        }
    }

    private bool IntersectsAnyPlaced(Rect2 rect)
    {
        for (var i = 0; i < _placedRectsScratch.Count; i++)
        {
            if (_placedRectsScratch[i].Intersects(rect))
                return true;
        }
        return false;
    }
}
