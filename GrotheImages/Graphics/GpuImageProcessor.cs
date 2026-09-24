using System;

namespace GrotheImages;

/// <summary>
/// The two image spaces of the engine:
/// <list type="bullet">
/// <item><description><b>User Image</b> - a rectangle of pixels in caller memory (<c>scan0</c>/<c>stride</c>).</description></item>
/// <item><description><b>Grothe Image</b> - the tiled logical image a <see cref="GrotheImage"/> stores.</description></item>
/// </list>
/// <c>Update</c> moves a User Image into the Grothe Image, <c>Load</c> renders the Grothe Image into a User
/// Image. Both take a transform in the direction the method names: <c>Update</c> takes User Image to Grothe
/// Image, <c>Load</c> takes Grothe Image to User Image.
/// </summary>
internal static class GpuImageProcessor
{
    /// <summary>Name of the transform parameter of every public entry point that takes a matrix.</summary>
    public const string TransformParameterName = "transformMatrix";

    /// <summary>Copies a User Image into a layer of the Grothe Image using a User Image to Grothe Image transform.</summary>
    public static void Update(GrotheImage image, int layerIndex, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix userToGrothe)
    {
        image.GetUpdateProgram().Execute(image, layerIndex, scan0, width, height, stride, format, userToGrothe);
    }

    /// <summary>Renders a layer or a composition of the Grothe Image into a User Image using a Grothe Image to User Image transform.</summary>
    public static void Load(GrotheImage image, int layerIndex, LayerCompose compose, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix grotheToUser)
    {
        if (!grotheToUser.IsFinite || !grotheToUser.TryInvert(out TransformMatrix userToGrothe))
            throw new ArgumentException("The transform matrix must be finite and invertible.", TransformParameterName);
        (compose == null ? image.GetLoadProgram() : compose.GetLoadProgram())
            .Execute(layerIndex, scan0, width, height, stride, format, userToGrothe);
    }

    public static void BlendSeams(GrotheImage image)
    {
        image.GetBlendProgram().Execute();
    }

    public static void UpdateTile(GrotheImage image, int layerIndex, long tileRow, long tileColumn, nint scan0, int width, int height, int stride, PixelFormat format)
    {
        if (format == image.Format && (format == PixelFormat.Bgra32 || format == PixelFormat.Rgba32 || format == PixelFormat.Gray8))
        {
            LayerStore store = image.GetLayer(layerIndex);
            TileArrayPage page = store.GetPageForTile(tileRow, tileColumn, out int slice);
            image.Graphics.Context.UpdateSubresource(page.Color.Texture, slice, null, scan0, stride, 0);
            store.MarkWritten(tileRow, tileColumn);
            return;
        }

        image.GetUpdateProgram().ExecuteTile(image, layerIndex, tileRow, tileColumn, scan0, width, height, stride, format);
    }
}
