using Godot;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Client.Map;

/// <summary>
/// Builds a single-draw-call Godot ArrayMesh from precomputed shared-arc BorderRibbonVertices.
/// Packs BorderFlags directly into Color.B (flags / 255f) so coastal, land, and maritime borders
/// evaluate dynamically on the GPU.
/// </summary>
public static class BorderMeshBuilder
{
    public static ArrayMesh? Build(BakedMapArtifact baked, Vector2 worldCanvasSize)
    {
        if (baked.BorderVertices.Length == 0 || baked.BorderIndices.Length == 0)
            return null;

        var vCount = baked.BorderVertices.Length;
        var positions = new Vector2[vCount];
        var uvs = new Vector2[vCount];
        var colors = new Color[vCount];

        for (var i = 0; i < vCount; i++)
        {
            var bv = baked.BorderVertices[i];
            positions[i] = new Vector2(bv.X * worldCanvasSize.X, bv.Y * worldCanvasSize.Y);
            uvs[i] = new Vector2(bv.LeftFaceId, bv.RightFaceId);
            colors[i] = new Color(
                Math.Clamp(0.5f + 0.25f * bv.OffsetX, 0f, 1f),
                Math.Clamp(0.5f + 0.25f * bv.OffsetY, 0f, 1f),
                bv.BorderFlags / 255.0f,
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
        return mesh;
    }
}
