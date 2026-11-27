using Godot;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Client.Map;

/// <summary>
/// Resolves world-space pointer hit-testing against baked 16-bit LocationIdGrid + LocationKindGrid
/// (supports Habitable, Corridor, and SeaZone selection while filtering Impassable wasteland).
/// </summary>
public sealed class MapInputController
{
    private readonly Vector2 _worldCanvasSize;

    public MapInputController(Vector2 worldCanvasSize)
    {
        _worldCanvasSize = worldCanvasSize;
    }

    public int LookupLocationAtWorldPos(BakedMapArtifact? baked, Vector2 worldPos)
    {
        if (baked is null)
            return 0;

        var nx = worldPos.X / _worldCanvasSize.X;
        var ny = worldPos.Y / _worldCanvasSize.Y;
        if (nx < 0f || nx >= 1f || ny < 0f || ny >= 1f)
            return 0;

        var px = Math.Clamp((int)(nx * baked.Width), 0, baked.Width - 1);
        var py = Math.Clamp((int)(ny * baked.Height), 0, baked.Height - 1);
        var idx = py * baked.Width + px;

        if (baked.LocationKindGrid[idx] == (byte)LocationKind.Impassable)
            return 0;

        var locId = baked.LocationIdGrid[idx];
        return locId > 0 ? locId : baked.ProvinceIdGrid[idx];
    }
}
