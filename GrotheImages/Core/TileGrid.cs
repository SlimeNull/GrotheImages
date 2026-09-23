using System;

namespace GrotheImages;

internal readonly struct TileSampleAddress
{
    public TileSampleAddress(long row, long column, double localX, double localY)
    {
        Row = row;
        Column = column;
        LocalX = localX;
        LocalY = localY;
    }

    public long Row { get; }
    public long Column { get; }
    public double LocalX { get; }
    public double LocalY { get; }
}

internal static class TileGrid
{
    public static bool TryGetSamplingAddress(GrotheImageInfo info, double x, double y, out TileSampleAddress address)
    {
        if (x < 0 || y < 0 || x >= info.Width || y >= info.Height)
        {
            address = default(TileSampleAddress);
            return false;
        }

        double halfX = info.TileOverlapX / 2.0;
        double halfY = info.TileOverlapY / 2.0;
        long column = Clamp((long)Math.Floor((x - halfX) / info.StepX), 0, info.TileColumns - 1);
        long row = Clamp((long)Math.Floor((y - halfY) / info.StepY), 0, info.TileRows - 1);
        address = new TileSampleAddress(
            row,
            column,
            x - column * info.StepX,
            y - row * info.StepY);
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
}
