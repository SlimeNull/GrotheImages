using System;

namespace GrotheImages;

/// <summary>
/// Geometry of a Grothe Image: the equal-size tile grid, the overlap between neighbouring tiles and the
/// logical size they add up to. The logical image is
/// <c>TileWidth * TileColumns - (TileColumns - 1) * TileOverlapX</c> pixels wide (and the same for y).
/// </summary>
/// <remarks>
/// The geometry is independent of <see cref="PixelFormat"/>; the storage format is supplied separately to
/// <see cref="GrotheImage"/> and its alignment requirements are validated there.
/// </remarks>
public readonly struct GrotheImageInfo
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
        TileColumns = FindColumns(width, maxTileWidth, tileOverlapX, nameof(width));
        TileRows = FindColumns(height, maxTileHeight, tileOverlapY, nameof(height));
        TileWidth = ComputeTileSize(width, TileColumns, tileOverlapX);
        TileHeight = ComputeTileSize(height, TileRows, tileOverlapY);
        ValidateComputedValues();
    }

    /// <summary>
    /// Builds the geometry from an explicit tile grid. <see cref="FromTiles"/> is the public way in; this
    /// constructor skips the search for a tile size.
    /// </summary>
    private GrotheImageInfo(
        int tileWidth,
        int tileHeight,
        long tileRows,
        long tileColumns,
        int tileOverlapX,
        int tileOverlapY)
    {
        TileWidth = tileWidth;
        TileHeight = tileHeight;
        TileRows = tileRows;
        TileColumns = tileColumns;
        TileOverlapX = tileOverlapX;
        TileOverlapY = tileOverlapY;
        ValidateComputedValues();
    }

    /// <summary>
    /// Describes a Grothe Image by its tile grid: <paramref name="tileRows"/> x <paramref name="tileColumns"/>
    /// tiles of <paramref name="tileWidth"/> x <paramref name="tileHeight"/> pixels, overlapping by
    /// <paramref name="tileOverlapX"/> / <paramref name="tileOverlapY"/>.
    /// </summary>
    public static GrotheImageInfo FromTiles(
        int tileWidth,
        int tileHeight,
        long tileRows,
        long tileColumns,
        int tileOverlapX = 0,
        int tileOverlapY = 0)
    {
        return new GrotheImageInfo(tileWidth, tileHeight, tileRows, tileColumns, tileOverlapX, tileOverlapY);
    }

    public int TileWidth { get; }
    public int TileHeight { get; }
    public long TileRows { get; }
    public long TileColumns { get; }
    public int TileOverlapX { get; }
    public int TileOverlapY { get; }

    /// <summary>Logical image width in pixels.</summary>
    public long Width => checked((long)TileWidth * TileColumns - (long)(TileColumns - 1) * TileOverlapX);

    /// <summary>Logical image height in pixels.</summary>
    public long Height => checked((long)TileHeight * TileRows - (long)(TileRows - 1) * TileOverlapY);

    /// <summary>Distance between the origins of two neighbouring tile columns.</summary>
    internal long StepX => TileWidth - TileOverlapX;

    /// <summary>Distance between the origins of two neighbouring tile rows.</summary>
    internal long StepY => TileHeight - TileOverlapY;

    /// <summary>
    /// Rejects storage formats whose chroma planes need even tile geometry. The tile size itself is chosen
    /// without knowing the format, so this is checked when the format becomes known.
    /// </summary>
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

    /// <summary>
    /// The number of equal-size tiles that covers <paramref name="length"/> with a tile no larger than
    /// <paramref name="maxTile"/>, or throws when no such layout exists.
    /// </summary>
    /// <remarks>
    /// Equal-size tiles mean the tile step has to divide <c>length - overlap</c> exactly, so the column
    /// count must be a divisor of that span. The search picks the divisor closest above
    /// <c>ceil(span / (maxTile - overlap))</c>, which is the largest tile that fits. A span whose divisors
    /// are all far away from that bound - a prime span, for example - would otherwise turn into millions of
    /// one-pixel tiles, so a layout whose tile is smaller than half of the requested maximum is rejected
    /// instead of silently accepted. Callers that need such a size can pass the tile size they want
    /// directly to <see cref="FromTiles"/>.
    /// </remarks>
    private static long FindColumns(long length, int maxTile, int overlap, string lengthName)
    {
        long span = checked(length - overlap);
        long maxStep = maxTile - overlap;
        long minimumColumns = (span + maxStep - 1) / maxStep;
        if (minimumColumns < 1) minimumColumns = 1;

        // Every divisor of the span is a column count that splits it into equal tile steps. Trial division
        // up to the square root visits them all, including the co-divisor, in O(sqrt(span)).
        long best = 0;
        for (long divisor = 1; divisor <= span / divisor; divisor++)
        {
            if (span % divisor != 0) continue;
            long coDivisor = span / divisor;
            if (divisor >= minimumColumns && (best == 0 || divisor < best)) best = divisor;
            if (coDivisor >= minimumColumns && (best == 0 || coDivisor < best)) best = coDivisor;
        }

        if (best == 0)
            throw new ArgumentException("A logical size of " + length + " cannot be covered by equal-size tiles of at most " + maxTile + " pixels.", lengthName);
        if (span / best * 2 < maxStep)
            throw new ArgumentException(
                "A logical size of " + length + " with a maximum tile size of " + maxTile + " has no usable equal-size layout: "
                + "the best split uses " + best + " tiles of " + (overlap + span / best) + " pixels, less than half of the maximum. "
                + "Choose a maximum tile size whose divisors are closer to " + (span / minimumColumns + overlap) + " pixels, or use FromTiles.",
                lengthName);
        return best;
    }

    private static int ComputeTileSize(long length, long count, int overlap)
    {
        long value = checked(overlap + (length - overlap) / count);
        if (value <= overlap || value > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length));
        return (int)value;
    }

    internal void ValidateComputedValues()
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
