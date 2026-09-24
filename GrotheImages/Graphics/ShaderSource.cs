using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SharpGen.Runtime;
using Vortice.Direct3D;

namespace GrotheImages;

/// <summary>
/// The HLSL sources shipped inside the assembly. Every shader lives in <c>Shaders/</c> and pulls in the
/// parts it needs with <c>#include</c>: <c>"name.hlsl"</c> for the files on disk, <c>&lt;macros&gt;</c>
/// for the macros generated for one compilation.
/// </summary>
internal static class ShaderSource
{
    private static readonly Regex Separators = new Regex(@"\\|/", RegexOptions.Compiled);

    /// <summary>Reads one embedded shader file, for example <c>Load.hlsl</c>.</summary>
    public static string Load(string fileName)
    {
        using (Stream stream = OpenStream(fileName))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    /// <summary>Opens one embedded shader file; the name may be a path relative to the project.</summary>
    public static Stream OpenStream(string fileName)
    {
        string resource = "GrotheImages.Shaders." + Separators.Replace(fileName, ".");
        Stream stream = typeof(ShaderSource).Assembly.GetManifestResourceStream(resource);
        if (stream == null)
            throw new GrotheImageException("The embedded shader '" + fileName + "' (" + resource + ") is missing from the GrotheImages assembly.");
        return stream;
    }

    /// <summary>Formats macro definitions as the body of the generated <c>macros</c> include file.</summary>
    public static string Macros(IEnumerable<KeyValuePair<string, string>> macros)
    {
        var b = new StringBuilder();
        foreach (KeyValuePair<string, string> macro in macros)
            b.Append("#define ").Append(macro.Key).Append(' ').Append(macro.Value ?? string.Empty).Append('\n');
        return b.ToString();
    }
}

/// <summary>
/// Resolves <c>#include</c> for a single compilation: the source generated for this draw (the macros) is
/// served from memory, every other include comes from the embedded shaders. SharpGen callbacks derive
/// from <see cref="CallbackBase"/>.
/// </summary>
internal sealed class ShaderInclude : CallbackBase, Include
{
    private readonly List<KeyValuePair<string, string>> _generated = new List<KeyValuePair<string, string>>();
    private readonly Dictionary<string, string> _byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ShaderInclude(IEnumerable<KeyValuePair<string, string>> generated)
    {
        foreach (KeyValuePair<string, string> pair in generated)
        {
            _generated.Add(pair);
            _byName[pair.Key] = pair.Value;
        }
    }

    /// <summary>The generated sources, so a failed compilation can carry them in its message.</summary>
    public string GeneratedSource
    {
        get
        {
            var b = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in _generated)
                b.Append("// ").Append(pair.Key).Append('\n').Append(pair.Value).Append('\n');
            return b.ToString();
        }
    }

    public Stream Open(IncludeType type, string fileName, Stream parentStream)
    {
        string code;
        if (_byName.TryGetValue(fileName, out code))
            return new MemoryStream(Encoding.UTF8.GetBytes(code), false);
        return ShaderSource.OpenStream(fileName);
    }

    public void Close(Stream stream) => stream?.Dispose();
}
