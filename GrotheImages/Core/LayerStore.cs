using System;
using System.Collections.Generic;
using System.Numerics;
using DxgiFormat = Vortice.DXGI.Format;

namespace GrotheImages;

internal sealed class LayerStore : IDisposable
{
    internal const int MaxArraySlices = 2048;

    private readonly D3D11DeviceContext _graphics;
    private readonly GrotheImageInfo _info;
    private readonly PixelFormat _format;
    private readonly Dictionary<long, TileArrayPage> _pages = new Dictionary<long, TileArrayPage>();
    private readonly HashSet<long> _writtenTiles = new HashSet<long>();

    public LayerStore(D3D11DeviceContext graphics, GrotheImageInfo info, PixelFormat format)
    {
        _graphics = graphics;
        _info = info;
        _format = format;
        PageCount = checked((info.TileRows * info.TileColumns + MaxArraySlices - 1) / MaxArraySlices);
    }

    public long PageCount { get; }

    public IReadOnlyCollection<long> WrittenTiles => _writtenTiles;

    public void MarkWritten(long row, long column)
    {
        _writtenTiles.Add(TileGrid.GetLinearIndex(_info, row, column));
    }

    /// <summary>True when the tile already holds data, used to avoid blending zeros into a neighbour.</summary>
    public bool IsWritten(long row, long column)
    {
        return _writtenTiles.Contains(TileGrid.GetLinearIndex(_info, row, column));
    }

    /// <summary>The page that holds a tile, creating and allocating it when this is the first use.</summary>
    public TileArrayPage GetPageForTile(long row, long column, out int slice)
    {
        long linear = TileGrid.GetLinearIndex(_info, row, column);
        long pageIndex = linear / MaxArraySlices;
        slice = (int)(linear % MaxArraySlices);
        return GetOrCreatePage(pageIndex);
    }

    /// <summary>
    /// The page that holds a tile, or <c>false</c> when that page was never allocated. Read paths use this
    /// so that reading a layer cannot create GPU resources as a side effect.
    /// </summary>
    public bool TryGetPageForTile(long row, long column, out TileArrayPage page, out int slice)
    {
        long linear = TileGrid.GetLinearIndex(_info, row, column);
        slice = (int)(linear % MaxArraySlices);
        return _pages.TryGetValue(linear / MaxArraySlices, out page);
    }

    public TileArrayPage GetOrCreatePage(long pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        TileArrayPage page;
        if (_pages.TryGetValue(pageIndex, out page)) return page;

        long totalTiles = checked(_info.TileRows * _info.TileColumns);
        int arraySize = (int)Math.Min(MaxArraySlices, totalTiles - pageIndex * MaxArraySlices);
        page = new TileArrayPage(_graphics, _info, _format, arraySize);
        _pages.Add(pageIndex, page);
        return page;
    }

    public void Dispose()
    {
        foreach (TileArrayPage page in _pages.Values) page.Dispose();
        _pages.Clear();
    }
}

internal sealed class TileArrayPage : IDisposable
{
    /// <summary>Value an untouched RGB or Gray8 tile holds: every channel zero, so alpha is transparent.</summary>
    private static readonly Vector4 EmptyColor = new Vector4(0, 0, 0, 0);

    /// <summary>Value an untouched YUV tile holds: luma zero and neutral chroma, as the format documents.</summary>
    private static readonly Vector4 EmptyChroma = new Vector4(0.5f, 0.5f, 0, 0);

    /// <summary>
    /// Allocates a page and initialises every slice to the empty value of the storage format. Creating the
    /// page is what makes the tiles readable, so the clear is what guarantees that a tile nobody wrote reads
    /// back as the documented zero value instead of uninitialised GPU memory. Must be called with the shared
    /// command lock held.
    /// </summary>
    public TileArrayPage(D3D11DeviceContext graphics, GrotheImageInfo info, PixelFormat format, int arraySize)
    {
        ArraySize = arraySize;
        switch (format)
        {
            case PixelFormat.Gray8:
                Color = new TileArrayResource(graphics, DxgiFormat.R8_UNorm, info.TileWidth, info.TileHeight, arraySize);
                break;
            case PixelFormat.Bgra32:
                Color = new TileArrayResource(graphics, DxgiFormat.B8G8R8A8_UNorm, info.TileWidth, info.TileHeight, arraySize);
                break;
            case PixelFormat.Rgba32:
                Color = new TileArrayResource(graphics, DxgiFormat.R8G8B8A8_UNorm, info.TileWidth, info.TileHeight, arraySize);
                break;
            case PixelFormat.Yuv444:
            case PixelFormat.Yuv422:
            case PixelFormat.Yuv420:
                Y = new TileArrayResource(graphics, DxgiFormat.R8_UNorm, info.TileWidth, info.TileHeight, arraySize);
                Uv = new TileArrayResource(
                    graphics,
                    DxgiFormat.R8G8_UNorm,
                    info.TileWidth / PixelFormatRules.ChromaSubsampleX(format),
                    info.TileHeight / PixelFormatRules.ChromaSubsampleY(format),
                    arraySize);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        Clear(graphics, Color, EmptyColor);
        Clear(graphics, Y, EmptyColor);
        Clear(graphics, Uv, EmptyChroma);
    }

    public int ArraySize { get; }
    public TileArrayResource Color { get; }
    public TileArrayResource Y { get; }
    public TileArrayResource Uv { get; }

    public void Dispose()
    {
        Color?.Dispose();
        Y?.Dispose();
        Uv?.Dispose();
    }

    private static void Clear(D3D11DeviceContext graphics, TileArrayResource plane, Vector4 value)
    {
        if (plane != null) graphics.Context.ClearUnorderedAccessView(plane.UnorderedAccessView, value);
    }
}
