using System;
using System.Collections.Generic;
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

    public TileArrayPage GetPageForTile(long row, long column, out int slice)
    {
        long linear = TileGrid.GetLinearIndex(_info, row, column);
        long pageIndex = linear / LayerStore.MaxArraySlices;
        slice = (int)(linear % LayerStore.MaxArraySlices);
        return GetOrCreatePage(pageIndex);
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
    public TileArrayPage(D3D11DeviceContext graphics, GrotheImageInfo info, PixelFormat format, int arraySize)
    {
        ArraySize = arraySize;
        switch (format)
        {
            case PixelFormat.Gray8:
                Color = new TileArrayResource(graphics, DxgiFormat.R8_UNorm, info.TileWidth, info.TileHeight, arraySize, false);
                break;
            case PixelFormat.Bgra32:
                Color = new TileArrayResource(graphics, DxgiFormat.B8G8R8A8_UNorm, info.TileWidth, info.TileHeight, arraySize, false);
                break;
            case PixelFormat.Rgba32:
                Color = new TileArrayResource(graphics, DxgiFormat.R8G8B8A8_UNorm, info.TileWidth, info.TileHeight, arraySize, false);
                break;
            case PixelFormat.Yuv444:
                Y = new TileArrayResource(graphics, DxgiFormat.R8_UNorm, info.TileWidth, info.TileHeight, arraySize, false);
                Uv = new TileArrayResource(graphics, DxgiFormat.R8G8_UNorm, info.TileWidth, info.TileHeight, arraySize, false);
                break;
            case PixelFormat.Yuv422:
                Y = new TileArrayResource(graphics, DxgiFormat.R8_UNorm, info.TileWidth, info.TileHeight, arraySize, false);
                Uv = new TileArrayResource(graphics, DxgiFormat.R8G8_UNorm, info.TileWidth / 2, info.TileHeight, arraySize, false);
                break;
            case PixelFormat.Yuv420:
                Y = new TileArrayResource(graphics, DxgiFormat.R8_UNorm, info.TileWidth, info.TileHeight, arraySize, false);
                Uv = new TileArrayResource(graphics, DxgiFormat.R8G8_UNorm, info.TileWidth / 2, info.TileHeight / 2, arraySize, false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
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
}
