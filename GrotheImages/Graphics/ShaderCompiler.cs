using System;
using System.Runtime.InteropServices;
using System.Text;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace GrotheImages;

internal static class ShaderCompiler
{
    public static unsafe Blob Compile(string source, string entryPoint, string target)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Blob code;
            Blob errors;
            Result result = Compiler.Compile(
                (void*)handle.AddrOfPinnedObject(),
                bytes.Length,
                "GrotheImages.hlsl",
                null,
                null,
                entryPoint,
                target,
                ShaderFlags.None,
                EffectFlags.None,
                out code,
                out errors);
            if (result.Failure)
            {
                string message = errors == null ? result.Description : errors.AsString();
                errors?.Dispose();
                throw new GrotheImageException("HLSL compilation failed: " + message);
            }
            errors?.Dispose();
            return code;
        }
        finally
        {
            handle.Free();
        }
    }
}
