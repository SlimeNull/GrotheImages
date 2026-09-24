using System;

namespace GrotheImages;

/// <summary>
/// The area of Grothe Image space that a transformed User Image rectangle covers. A projective transform can
/// send a corner to infinity, so the region keeps its four corners: when they all stay on the same side of
/// the horizon the region is a bounded convex quad and the separating axis test is exact, otherwise only the
/// bounding box is meaningful and every tile inside it has to be accepted.
/// </summary>
internal readonly struct CoveredRegion
{
    private readonly double _x0, _y0, _x1, _y1, _x2, _y2, _x3, _y3;

    public CoveredRegion(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, bool bounded)
    {
        _x0 = x0;
        _y0 = y0;
        _x1 = x1;
        _y1 = y1;
        _x2 = x2;
        _y2 = y2;
        _x3 = x3;
        _y3 = y3;
        IsBounded = bounded;
        MinX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
        MaxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        MinY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
        MaxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
    }

    public double MinX { get; }
    public double MaxX { get; }
    public double MinY { get; }
    public double MaxY { get; }

    /// <summary>False when the rectangle crosses the horizon of the transform, which makes the region unbounded.</summary>
    public bool IsBounded { get; }

    /// <summary>
    /// The tile columns and rows whose sampling ownership band can hold a covered point. This is the range
    /// <c>Load</c> has to bind: the pixel shader picks a tile with the same ownership rule, so a tile outside
    /// this range can never be sampled.
    /// </summary>
    public void GetSamplingTileRange(GrotheImageInfo info, out long firstRow, out long lastRow, out long firstColumn, out long lastColumn)
    {
        double halfX = info.TileOverlapX / 2.0;
        double halfY = info.TileOverlapY / 2.0;
        firstColumn = TileGrid.ClampFloorToLong((MinX - halfX) / info.StepX, 0, info.TileColumns - 1);
        lastColumn = TileGrid.ClampFloorToLong((MaxX - halfX) / info.StepX, 0, info.TileColumns - 1);
        firstRow = TileGrid.ClampFloorToLong((MinY - halfY) / info.StepY, 0, info.TileRows - 1);
        lastRow = TileGrid.ClampFloorToLong((MaxY - halfY) / info.StepY, 0, info.TileRows - 1);
    }

    /// <summary>
    /// The tile columns and rows whose stored pixels can fall inside the region. <c>Update</c> writes every
    /// pixel of a tile it dispatches, so this range uses the storage rectangle of a tile, which is wider than
    /// its ownership band by the overlap.
    /// </summary>
    public void GetStorageTileRange(GrotheImageInfo info, out long firstRow, out long lastRow, out long firstColumn, out long lastColumn)
    {
        firstColumn = TileGrid.ClampFloorToLong((MinX - info.TileWidth) / info.StepX + 1.0, 0, info.TileColumns - 1);
        lastColumn = TileGrid.ClampFloorToLong(MaxX / info.StepX, 0, info.TileColumns - 1);
        firstRow = TileGrid.ClampFloorToLong((MinY - info.TileHeight) / info.StepY + 1.0, 0, info.TileRows - 1);
        lastRow = TileGrid.ClampFloorToLong(MaxY / info.StepY, 0, info.TileRows - 1);
    }

    /// <summary>
    /// True when a stored pixel of this tile can fall inside the region, which is the condition for the tile
    /// to receive a write. Tiles that only sit inside the bounding box of a rotated or perspective region are
    /// rejected, so an update never dispatches - and never marks as written - a tile it cannot paint.
    /// </summary>
    public bool IntersectsTile(GrotheImageInfo info, long row, long column)
    {
        double left = column * (double)info.StepX;
        double top = row * (double)info.StepY;
        double right = left + info.TileWidth;
        double bottom = top + info.TileHeight;

        // Projection onto the axes of the tile.
        if (MaxX < left || MinX > right || MaxY < top || MinY > bottom) return false;
        // A region that crosses the horizon is unbounded; its bounding box is all that is left to test.
        if (!IsBounded) return true;

        // Separating axis test against the four edges of the quad. A pixel centre inside the quad is strictly
        // inside its own tile rectangle, so this never rejects a tile that has a covered pixel.
        for (int edge = 0; edge < 4; edge++)
        {
            int next = (edge + 1) & 3;
            double ax = GetX(edge), ay = GetY(edge);
            double normalX = ay - GetY(next);
            double normalY = GetX(next) - ax;

            double quadMin = double.MaxValue, quadMax = double.MinValue;
            for (int corner = 0; corner < 4; corner++)
            {
                double projection = GetX(corner) * normalX + GetY(corner) * normalY;
                quadMin = Math.Min(quadMin, projection);
                quadMax = Math.Max(quadMax, projection);
            }

            double c0 = left * normalX + top * normalY;
            double c1 = right * normalX + top * normalY;
            double c2 = right * normalX + bottom * normalY;
            double c3 = left * normalX + bottom * normalY;
            double tileMin = Math.Min(Math.Min(c0, c1), Math.Min(c2, c3));
            double tileMax = Math.Max(Math.Max(c0, c1), Math.Max(c2, c3));
            if (quadMax < tileMin || tileMax < quadMin) return false;
        }
        return true;
    }

    private double GetX(int corner) => corner == 0 ? _x0 : corner == 1 ? _x1 : corner == 2 ? _x2 : _x3;

    private double GetY(int corner) => corner == 0 ? _y0 : corner == 1 ? _y1 : corner == 2 ? _y2 : _y3;
}

internal static class TileGrid
{
    /// <summary>
    /// Maps the four corners of a <paramref name="width"/> x <paramref name="height"/> rectangle through
    /// <paramref name="matrix"/>, in order, and reports whether every corner has an image. Returns
    /// <c>false</c> when a corner lies on the horizon of the transform.
    /// </summary>
    public static bool TryGetCoveredRegion(TransformMatrix matrix, double width, double height, out CoveredRegion region)
    {
        var xs = new double[4];
        var ys = new double[4];
        bool positiveDivisor = false;
        bool negativeDivisor = false;
        for (int corner = 0; corner < 4; corner++)
        {
            double x = corner == 1 || corner == 2 ? width : 0;
            double y = corner == 2 || corner == 3 ? height : 0;
            if (!matrix.TryTransformPoint(x, y, out double transformedX, out double transformedY, out double divisor))
            {
                region = default(CoveredRegion);
                return false;
            }
            xs[corner] = transformedX;
            ys[corner] = transformedY;
            if (divisor > 0) positiveDivisor = true;
            else negativeDivisor = true;
        }
        // Corners on opposite sides of the horizon mean the rectangle passes through infinity: the preimage
        // is not a bounded quad, so only the bounding box can be used.
        region = new CoveredRegion(xs[0], ys[0], xs[1], ys[1], xs[2], ys[2], xs[3], ys[3], !(positiveDivisor && negativeDivisor));
        return true;
    }

    public static long GetLinearIndex(GrotheImageInfo info, long row, long column)
    {
        if (row < 0 || row >= info.TileRows) throw new ArgumentOutOfRangeException(nameof(row));
        if (column < 0 || column >= info.TileColumns) throw new ArgumentOutOfRangeException(nameof(column));
        return checked(row * info.TileColumns + column);
    }

    public static long Clamp(long value, long min, long max)
    {
        return value < min ? min : (value > max ? max : value);
    }

    /// <summary>
    /// <see cref="Math.Floor(double)"/> clamped into a tile index range. Clamping before the conversion keeps
    /// a coordinate that a perspective transform threw to infinity from wrapping around, and <c>min</c> wins
    /// for a NaN.
    /// </summary>
    internal static long ClampFloorToLong(double value, long min, long max)
    {
        if (double.IsNaN(value) || value <= min) return min;
        if (value >= max) return max;
        return (long)Math.Floor(value);
    }
}
