using System;
using Vortice.Direct3D11;
using DxgiFormat = Vortice.DXGI.Format;

namespace GrotheImages;

internal sealed class TileArrayResource : IDisposable
{
    public TileArrayResource(D3D11DeviceContext graphics, DxgiFormat format, int width, int height, int arraySize, bool renderTarget)
    {
        if (arraySize <= 0) throw new ArgumentOutOfRangeException(nameof(arraySize));
        Texture = graphics.Device.CreateTexture2D(
            format,
            width,
            height,
            arraySize,
            1,
            null,
            renderTarget ? BindFlags.ShaderResource | BindFlags.RenderTarget : BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            ResourceOptionFlags.None,
            ResourceUsage.Default,
            CpuAccessFlags.None);
        ShaderResourceView = graphics.Device.CreateShaderResourceView(Texture, null);
        UnorderedAccessView = graphics.Device.CreateUnorderedAccessView(Texture, null);
        Format = format;
        Width = width;
        Height = height;
        ArraySize = arraySize;
    }

    public DxgiFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int ArraySize { get; }
    public ID3D11Texture2D Texture { get; }
    public ID3D11ShaderResourceView ShaderResourceView { get; }
    public ID3D11UnorderedAccessView UnorderedAccessView { get; }

    public void Dispose()
    {
        ShaderResourceView.Dispose();
        UnorderedAccessView.Dispose();
        Texture.Dispose();
    }
}
