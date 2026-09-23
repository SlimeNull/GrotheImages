using System;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace GrotheImages;

internal sealed class D3D11DeviceContext : IDisposable
{
    public D3D11DeviceContext()
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
        };
        FeatureLevel featureLevel;
        ID3D11Device device;
        ID3D11DeviceContext context;
        Result result = D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out device,
            out featureLevel,
            out context);
        result.CheckError();
        Device = device;
        Context = context;
        FeatureLevel = featureLevel;
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public FeatureLevel FeatureLevel { get; }

    public void Dispose()
    {
        Context?.Dispose();
        Device?.Dispose();
    }
}
