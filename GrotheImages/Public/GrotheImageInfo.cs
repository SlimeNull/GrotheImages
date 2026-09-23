using System;

namespace GrotheImages;

public sealed class GrotheImageInfo
{
    public GrotheImageInfo(
        long width,
        long height,
        int maxTileWidth,
        int maxTileHeight,
        int tileOverlapX = 0,
        int tileOverlapY = 0)
    {
        ValidateAxisRequest(width, maxTileWidth, tileOverlapX, nameof(width), nameof(maxTileWidth), nameof(tileOverlapX));
        ValidateAxisRequest(height, maxTileHeight, tileOverlapY, nameof(height), nameof(maxTileHeight), nameof(tileOverlapY));

        TileOverlapX = tileOverlapX;
        TileOverlapY = tileOverlapY;
        TileColumns = FindColumns(width, maxTileWidth, tileOverlapX);
        TileRows = FindColumns(height, maxTileHeight, tileOverlapY);
        TileWidth = ComputeTileSize(width, TileColumns, tileOverlapX);
        TileHeight = ComputeTileSize(height, TileRows, tileOverlapY);
        ValidateComputedValues();
    }

    private GrotheImageInfo(
        int tileWidth,
        int tileHeight,
        long tileRows,
        long tileColumns,
        int tileOverlapX,
        int tileOverlapY,
        bool fromTileLayout)
    {
        TileWidth = tileWidth;
        TileHeight = tileHeight;
        TileRows = tileRows;
        TileColumns = tileColumns;
        TileOverlapX = tileOverlapX;
        TileOverlapY = tileOverlapY;
        ValidateComputedValues();
    }

    public static GrotheImageInfo FromTiles(
        int tileWidth,
        int tileHeight,
        long tileRows,
        long tileColumns,
        int tileOverlapX = 0,
        int tileOverlapY = 0)
    {
        return new GrotheImageInfo(tileWidth, tileHeight, tileRows, tileColumns, tileOverlapX, tileOverlapY, true);
    }

    public int TileWidth { get; }
    public int TileHeight { get; }
    public long TileRows { get; }
    public long TileColumns { get; }
    public int TileOverlapX { get; }
    public int TileOverlapY { get; }

    public long Width => checked((long)TileWidth * TileColumns - (long)(TileColumns - 1) * TileOverlapX);
    public long Height => checked((long)TileHeight * TileRows - (long)(TileRows - 1) * TileOverlapY);

    internal long StepX => TileWidth - TileOverlapX;
    internal long StepY => TileHeight - TileOverlapY;

    internal void ValidateForFormat(PixelFormat format)
    {
        if (format == PixelFormat.Yuv422)
        {
            if ((Width & 1) != 0 || (TileWidth & 1) != 0 || (TileOverlapX & 1) != 0)
                throw new ArgumentException("Yuv422 requires an even image width, tile width, and horizontal overlap.", nameof(format));
        }
        else if (format == PixelFormat.Yuv420)
        {
            if ((Width & 1) != 0 || (Height & 1) != 0
                || (TileWidth & 1) != 0 || (TileHeight & 1) != 0
                || (TileOverlapX & 1) != 0 || (TileOverlapY & 1) != 0)
                throw new ArgumentException("Yuv420 requires even image dimensions, tile dimensions, and overlaps.", nameof(format));
        }
    }

    private static void ValidateAxisRequest(long length, int maxTile, int overlap, string lengthName, string maxName, string overlapName)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(lengthName);
        if (maxTile <= 0) throw new ArgumentOutOfRangeException(maxName);
        if (overlap < 0 || overlap >= maxTile) throw new ArgumentOutOfRangeException(overlapName);
    }

    private static long FindColumns(long length, int maxTile, int overlap)
    {
        long n = checked(length - overlap);
        long minimumColumns = (n + (maxTile - overlap) - 1) / (maxTile - overlap);
        for (long columns = Math.Max(1, minimumColumns); columns <= n; columns++)
        {
            if (n % columns == 0)
                return columns;
            if (columns > 10000000 && n > 10000000)
                break;
        }
        throw new ArgumentException("The requested logical size cannot be represented by equal-size tiles and the supplied maximum tile size.");
    }

    private static int ComputeTileSize(long length, long count, int overlap)
    {
        long value = checked(overlap + (length - overlap) / count);
        if (value <= overlap || value > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length));
        return (int)value;
    }

    private void ValidateComputedValues()
    {
        if (TileWidth <= 0) throw new ArgumentOutOfRangeException(nameof(TileWidth));
        if (TileHeight <= 0) throw new ArgumentOutOfRangeException(nameof(TileHeight));
        if (TileRows <= 0) throw new ArgumentOutOfRangeException(nameof(TileRows));
        if (TileColumns <= 0) throw new ArgumentOutOfRangeException(nameof(TileColumns));
        if (TileOverlapX < 0 || TileOverlapX >= TileWidth) throw new ArgumentOutOfRangeException(nameof(TileOverlapX));
        if (TileOverlapY < 0 || TileOverlapY >= TileHeight) throw new ArgumentOutOfRangeException(nameof(TileOverlapY));
        _ = Width;
        _ = Height;
    }
}
