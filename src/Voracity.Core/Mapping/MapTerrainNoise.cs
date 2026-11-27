namespace Voracity.Core.Mapping;

public static class MapTerrainNoise
{
    public static byte TerrainNameToId(string terrain) => terrain.ToLowerInvariant() switch
    {
        "plains" or "flatland" or "grasslands" => 1,
        "farmlands" or "farmland" => 2,
        "forest" or "woods" => 3,
        "hills" or "plateau" => 4,
        "mountains" => 5,
        "wetlands" => 6,
        "desert" or "arid" => 7,
        "coastal" => 8,
        "tundra" => 4,
        _ => 1
    };

    public static float Fbm2D(float x, float y)
    {
        return 0.60f * ValueNoise2D(x, y)
             + 0.28f * ValueNoise2D(x * 2.07f + 11.3f, y * 2.03f + 7.7f)
             + 0.12f * ValueNoise2D(x * 4.19f + 23.1f, y * 4.11f + 17.9f);
    }

    private static float ValueNoise2D(float x, float y)
    {
        var ix = (int)MathF.Floor(x);
        var iy = (int)MathF.Floor(y);
        var fx = x - ix;
        var fy = y - iy;

        var u = fx * fx * (3f - 2f * fx);
        var v = fy * fy * (3f - 2f * fy);

        var a = Hash01(ix, iy);
        var b = Hash01(ix + 1, iy);
        var c = Hash01(ix, iy + 1);
        var d = Hash01(ix + 1, iy + 1);

        var lerp1 = a + (b - a) * u;
        var lerp2 = c + (d - c) * u;
        return (lerp1 + (lerp2 - lerp1) * v) * 2f - 1f;
    }

    private static float Hash01(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0x00FFFFFFu) / 16777215.0f;
        }
    }
}
