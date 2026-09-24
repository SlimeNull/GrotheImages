using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GrotheImages;

/// <summary>
/// One overload of a <c>Shaders/Common.hlsl</c> function. The first parameter is always the operand
/// supplied by the layer reference, the remaining parameters are the call arguments written in the
/// expression.
/// </summary>
internal readonly struct MemberOverload
{
    public MemberOverload(int[] parameterWidths, int resultWidth)
    {
        ParameterWidths = parameterWidths;
        ResultWidth = resultWidth;
    }

    public int[] ParameterWidths { get; }

    public int ResultWidth { get; }

    public int OperandWidth => ParameterWidths[0];

    public int ArgumentCount => ParameterWidths.Length - 1;

    public override string ToString()
    {
        var b = new StringBuilder();
        b.Append('(');
        for (int i = 0; i < ParameterWidths.Length; i++)
        {
            if (i > 0) b.Append(", ");
            b.Append("float").Append(ParameterWidths[i]);
        }
        return b.Append(") -> float").Append(ResultWidth).ToString();
    }
}

/// <summary>
/// The HLSL helper library shared by the generated shaders. <c>Shaders/Common.hlsl</c> is embedded in
/// the assembly and compiled into every shader that needs it.
/// </summary>
internal static class ShaderLibrary
{
    // float foo(float3 v) / float4 foo(float2 a, float b) written one signature per line.
    private static readonly Regex SignaturePattern = new Regex(
        @"^[ \t]*(float[234]?)[ \t]+([A-Za-z_][A-Za-z0-9_]*)[ \t]*\(([^)]*)\)[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // HLSL intrinsics the reference engine registers as filters (RegisterIntrinsicFunctions). They are
    // not declared in Common.hlsl, so they get their own table: same width in and out, except the ones
    // that always reduce to a scalar.
    private static readonly string[] Intrinsics = { "abs", "saturate", "sqrt", "sign" };

    private static readonly string[] ScalarIntrinsics = { "length" };

    private static readonly Lazy<string> LazySource = new Lazy<string>(LoadSource);

    private static readonly Lazy<Dictionary<string, List<MemberOverload>>> LazyMembers =
        new Lazy<Dictionary<string, List<MemberOverload>>>(() => ParseMembers(LazySource.Value));

    /// <summary>The complete source of the embedded <c>Common.hlsl</c>.</summary>
    public static string Source => LazySource.Value;

    /// <summary>
    /// Every function declared in <c>Common.hlsl</c> becomes a layer member, plus the HLSL intrinsics the
    /// reference engine exposes. The shader file stays the single source of truth for the library itself.
    /// </summary>
    public static bool TryGetMember(string name, out List<MemberOverload> overloads)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        return LazyMembers.Value.TryGetValue(name, out overloads);
    }

    /// <summary>Available member names in ordinal order, used for diagnostics.</summary>
    public static IReadOnlyList<string> MemberNames =>
        LazyMembers.Value.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>A human readable list of a member's overloads, used in error messages.</summary>
    public static string DescribeOverloads(List<MemberOverload> overloads)
    {
        return string.Join(", ", overloads.Select(x => x.ToString()).ToArray());
    }

    /// <summary>
    /// True for the HLSL intrinsics, which the generated shader can call without <c>Common.hlsl</c>.
    /// </summary>
    public static bool IsIntrinsic(string name)
    {
        return Intrinsics.Contains(name) || ScalarIntrinsics.Contains(name);
    }

    private static string LoadSource() => ShaderSource.Load("Common.hlsl");

    private static Dictionary<string, List<MemberOverload>> ParseMembers(string source)
    {
        var members = new Dictionary<string, List<MemberOverload>>(StringComparer.Ordinal);
        foreach (Match match in SignaturePattern.Matches(source))
        {
            int resultWidth = GetWidth(match.Groups[1].Value);
            string name = match.Groups[2].Value;
            var parameterWidths = new List<int>();
            bool supported = resultWidth > 0;
            if (supported)
            {
                foreach (string parameter in match.Groups[3].Value.Split(','))
                {
                    string text = parameter.Trim();
                    if (text.Length == 0) continue;
                    int separator = text.IndexOfAny(new[] { ' ', '\t' });
                    int width = GetWidth(separator < 0 ? text : text.Substring(0, separator));
                    if (width == 0)
                    {
                        // A parameter the expression language cannot express (int, bool, ...) makes the
                        // whole overload invisible instead of silently generating broken HLSL.
                        supported = false;
                        break;
                    }
                    parameterWidths.Add(width);
                }
            }
            // A member always receives the operand as its first parameter.
            if (!supported || parameterWidths.Count == 0) continue;

            List<MemberOverload> overloads;
            if (!members.TryGetValue(name, out overloads))
            {
                overloads = new List<MemberOverload>();
                members.Add(name, overloads);
            }
            var overload = new MemberOverload(parameterWidths.ToArray(), resultWidth);
            if (!overloads.Any(x => x.ToString() == overload.ToString())) overloads.Add(overload);
        }
        if (members.Count == 0)
            throw new GrotheImageException("The embedded shader library 'Common.hlsl' declares no usable member functions.");
        foreach (string name in Intrinsics) AddIntrinsics(members, name, sameWidth: true);
        foreach (string name in ScalarIntrinsics) AddIntrinsics(members, name, sameWidth: false);
        return members;
    }

    /// <summary>Registers one intrinsic for every operand width the expression language can produce.</summary>
    private static void AddIntrinsics(Dictionary<string, List<MemberOverload>> members, string name, bool sameWidth)
    {
        var overloads = new List<MemberOverload>();
        for (int width = 1; width <= 4; width++)
            overloads.Add(new MemberOverload(new[] { width }, sameWidth ? width : 1));
        members[name] = overloads;
    }

    private static int GetWidth(string type)
    {
        switch (type)
        {
            case "float": return 1;
            case "float2": return 2;
            case "float3": return 3;
            case "float4": return 4;
            default: return 0;
        }
    }
}
