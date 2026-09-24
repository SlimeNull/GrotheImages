using System;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace GrotheImages;

/// <summary>
/// The process-wide Direct3D 11 device and its immediate context. A device is expensive to create and a
/// device has exactly one immediate context, which is not thread safe, so every Grothe Image shares one
/// device and all command submission is serialized on <see cref="CommandLock"/>. Instances are reference
/// counted and the device is released when the last image that used it is disposed.
/// </summary>
internal sealed class D3D11DeviceContext : IDisposable
{
    /// <summary>
    /// Held while commands are recorded into the shared immediate context, and while the device is created
    /// or released. Always the innermost lock: take a <see cref="GrotheImage"/>'s own lock first.
    /// </summary>
    public static readonly object CommandLock = new object();

    private static ID3D11Device _device;
    private static ID3D11DeviceContext _context;
    private static FeatureLevel _featureLevel;
    private static int _references;

    private bool _disposed;

    public D3D11DeviceContext()
    {
        lock (CommandLock)
        {
            if (_device == null) CreateDevice();
            _references++;
            Device = _device;
            Context = _context;
            FeatureLevel = _featureLevel;
        }
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    /// <summary>The feature level of the shared device, which is always at least <c>11_0</c>.</summary>
    public FeatureLevel FeatureLevel { get; }

    public void Dispose()
    {
        lock (CommandLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (--_references > 0) return;
            _context?.Dispose();
            _device?.Dispose();
            _context = null;
            _device = null;
        }
    }

    private static void CreateDevice()
    {
        // Requesting only these two levels makes the device fail on an adapter that cannot run the
        // shader model 5 shaders and the typed UAVs this library needs.
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
        };
        Result result = D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device device,
            out FeatureLevel featureLevel,
            out ID3D11DeviceContext context);
        if (result.Failure || device == null || context == null)
        {
            context?.Dispose();
            device?.Dispose();
            throw new GrotheImageException("A Direct3D 11 hardware device with feature level 11_0 is required: " + result.Description);
        }
        if (featureLevel < FeatureLevel.Level_11_0)
        {
            context.Dispose();
            device.Dispose();
            throw new GrotheImageException("GrotheImages requires Direct3D feature level 11_0 or higher, but the device reports " + featureLevel + ".");
        }

        _device = device;
        _context = context;
        _featureLevel = featureLevel;
    }
}
