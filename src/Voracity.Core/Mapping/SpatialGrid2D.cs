namespace Voracity.Core.Mapping;

/// <summary>
/// Allocation-light uniform 2D spatial hash grid over normalized [0,1]x[0,1] map coordinates.
/// Used both for sub-second 2,500+ province Voronoi baking and for camera frustum culling in Voracity.Client.
/// </summary>
public sealed class SpatialGrid2D<T>
{
    private readonly int _cols;
    private readonly int _rows;
    private readonly List<int>[] _buckets;
    private readonly List<(float X, float Y, T Item)> _items = new();

    public SpatialGrid2D(int cols = 48, int rows = 28)
    {
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _buckets = new List<int>[_cols * _rows];
        for (var i = 0; i < _buckets.Length; i++)
            _buckets[i] = new List<int>(4);
    }

    public int Count => _items.Count;

    public void Clear()
    {
        _items.Clear();
        for (var i = 0; i < _buckets.Length; i++)
            _buckets[i].Clear();
    }

    public void Add(float normX, float normY, T item)
    {
        var idx = _items.Count;
        _items.Add((normX, normY, item));
        var cx = Math.Clamp((int)(normX * _cols), 0, _cols - 1);
        var cy = Math.Clamp((int)(normY * _rows), 0, _rows - 1);
        _buckets[cy * _cols + cx].Add(idx);
    }

    public void QueryRect(float minX, float minY, float maxX, float maxY, List<T> results)
    {
        results.Clear();
        var x0 = Math.Clamp((int)(minX * _cols), 0, _cols - 1);
        var y0 = Math.Clamp((int)(minY * _rows), 0, _rows - 1);
        var x1 = Math.Clamp((int)(maxX * _cols), 0, _cols - 1);
        var y1 = Math.Clamp((int)(maxY * _rows), 0, _rows - 1);

        for (var cy = y0; cy <= y1; cy++)
        {
            var rowBase = cy * _cols;
            for (var cx = x0; cx <= x1; cx++)
            {
                var bucket = _buckets[rowBase + cx];
                for (var i = 0; i < bucket.Count; i++)
                {
                    var entry = _items[bucket[i]];
                    if (entry.X >= minX && entry.X <= maxX && entry.Y >= minY && entry.Y <= maxY)
                        results.Add(entry.Item);
                }
            }
        }
    }

    public int FindNearest(float normX, float normY, float aspectX = 1.714f, float maxSearchRadiusNorm = 0.25f)
    {
        if (_items.Count == 0)
            return -1;

        var cx = Math.Clamp((int)(normX * _cols), 0, _cols - 1);
        var cy = Math.Clamp((int)(normY * _rows), 0, _rows - 1);
        var maxRing = Math.Max(2, (int)MathF.Ceiling(maxSearchRadiusNorm * Math.Max(_cols, _rows)));

        var bestItemIdx = -1;
        var bestDistSq = float.MaxValue;

        for (var ring = 0; ring <= maxRing; ring++)
        {
            var minCx = Math.Max(0, cx - ring);
            var maxCx = Math.Min(_cols - 1, cx + ring);
            var minCy = Math.Max(0, cy - ring);
            var maxCy = Math.Min(_rows - 1, cy + ring);

            for (var y = minCy; y <= maxCy; y++)
            {
                var onEdgeY = y == minCy || y == maxCy;
                var rowBase = y * _cols;
                for (var x = minCx; x <= maxCx; x++)
                {
                    if (!onEdgeY && x != minCx && x != maxCx)
                        continue;

                    var bucket = _buckets[rowBase + x];
                    for (var i = 0; i < bucket.Count; i++)
                    {
                        var itemIdx = bucket[i];
                        var e = _items[itemIdx];
                        var dx = (normX - e.X) * aspectX;
                        var dy = normY - e.Y;
                        var dSq = dx * dx + dy * dy;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestItemIdx = itemIdx;
                        }
                    }
                }
            }

            // Early out if ring lower bound exceeds best found Euclidean distance
            var minCellDx = Math.Max(0f, (ring - 1) / (float)_cols) * aspectX;
            var minCellDy = Math.Max(0f, (ring - 1) / (float)_rows);
            var minPossibleSq = MathF.Min(minCellDx * minCellDx, minCellDy * minCellDy);
            if (bestItemIdx >= 0 && minPossibleSq > bestDistSq)
                break;
        }

        if (bestItemIdx < 0)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var e = _items[i];
                var dx = (normX - e.X) * aspectX;
                var dy = normY - e.Y;
                var dSq = dx * dx + dy * dy;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestItemIdx = i;
                }
            }
        }

        return bestItemIdx;
    }

    public (float X, float Y, T Item) GetAt(int index) => _items[index];
}
