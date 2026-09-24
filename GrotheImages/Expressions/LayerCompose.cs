using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GrotheImages;

public sealed class LayerCompose : IDisposable
{
    private LoadProgram _loadProgram;
    private GrotheImage _owner;
    private bool _disposed;
    internal LayerCompose(string expression, ExpressionNode root, IReadOnlyCollection<int> referencedLayers, bool usesMemberExpression)
    {
        Expression = expression;
        Root = root;
        ReferencedLayerIndices = new ReadOnlyCollection<int>(referencedLayers.ToList());
        OutputChannelCount = root.Width;
        UsesMemberExpression = usesMemberExpression;
    }

    public string Expression { get; }
    public int OutputChannelCount { get; }

    /// <summary>True when the expression calls into <c>Shaders/Common.hlsl</c>, which is then compiled into the shader.</summary>
    internal bool UsesMemberExpression { get; }

    internal ExpressionNode Root { get; }
    internal ReadOnlyCollection<int> ReferencedLayerIndices { get; }
    internal GrotheImage Owner => _disposed ? null : _owner;

    internal void Attach(GrotheImage owner) => _owner = owner;

    internal LoadProgram GetLoadProgram()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LayerCompose));
        return _loadProgram ?? (_loadProgram = new LoadProgram(_owner, this));
    }

    internal void DisposeProgram()
    {
        _loadProgram?.Dispose();
        _loadProgram = null;
        _disposed = true;
        _owner = null;
    }

    public void Dispose()
    {
        GrotheImage owner = _owner;
        if (owner == null) return;
        lock (owner.SyncRoot)
        {
            if (_disposed) return;
            DisposeProgram();
            owner.ReleaseCompose(this);
        }
    }

    internal string ToHlsl() => Root.ToHlsl();
}

internal enum ExpressionTokenKind
{
    End,
    Number,
    Identifier,
    Dot,
    Comma,
    Plus,
    Minus,
    Star,
    Slash,
    LeftParen,
    RightParen,
}

internal readonly struct ExpressionToken
{
    public ExpressionToken(ExpressionTokenKind kind, string text, int position)
    {
        Kind = kind;
        Text = text;
        Position = position;
    }

    public ExpressionTokenKind Kind { get; }
    public string Text { get; }
    public int Position { get; }
}

internal sealed class ExpressionLexer
{
    private readonly string _text;
    private int _position;

    public ExpressionLexer(string text)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public ExpressionToken Next()
    {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
        if (_position >= _text.Length) return new ExpressionToken(ExpressionTokenKind.End, string.Empty, _position);

        int start = _position;
        char c = _text[_position++];
        if (c == '.' && _position < _text.Length && char.IsDigit(_text[_position]))
        {
            while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == 'e' || _text[_position] == 'E' || _text[_position] == '+' || _text[_position] == '-'))
            {
                char next = _text[_position];
                if ((next == '+' || next == '-') && _text[_position - 1] != 'e' && _text[_position - 1] != 'E') break;
                _position++;
            }
            return new ExpressionToken(ExpressionTokenKind.Number, _text.Substring(start, _position - start), start);
        }

        switch (c)
        {
            case '.': return new ExpressionToken(ExpressionTokenKind.Dot, ".", start);
            case ',': return new ExpressionToken(ExpressionTokenKind.Comma, ",", start);
            case '+': return new ExpressionToken(ExpressionTokenKind.Plus, "+", start);
            case '-': return new ExpressionToken(ExpressionTokenKind.Minus, "-", start);
            case '*': return new ExpressionToken(ExpressionTokenKind.Star, "*", start);
            case '/': return new ExpressionToken(ExpressionTokenKind.Slash, "/", start);
            case '(': return new ExpressionToken(ExpressionTokenKind.LeftParen, "(", start);
            case ')': return new ExpressionToken(ExpressionTokenKind.RightParen, ")", start);
        }

        if (char.IsLetter(c) || c == '_')
        {
            while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_')) _position++;
            return new ExpressionToken(ExpressionTokenKind.Identifier, _text.Substring(start, _position - start), start);
        }

        if (char.IsDigit(c) || c == '.')
        {
            while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.' || _text[_position] == 'e' || _text[_position] == 'E' || _text[_position] == '+' || _text[_position] == '-'))
            {
                char next = _text[_position];
                if ((next == '+' || next == '-') && _position > start && _text[_position - 1] != 'e' && _text[_position - 1] != 'E') break;
                _position++;
            }
            return new ExpressionToken(ExpressionTokenKind.Number, _text.Substring(start, _position - start), start);
        }

        throw Error("Unexpected character '" + c + "'.", start);
    }

    private FormatException Error(string message, int position)
    {
        return new FormatException(message + " Position: " + position + ".");
    }
}

internal readonly struct ExpressionType
{
    public ExpressionType(int width)
    {
        if (width < 1 || width > 4) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
    }

    public int Width { get; }
}

internal abstract class ExpressionNode
{
    protected ExpressionNode(ExpressionType type)
    {
        Type = type;
    }

    public ExpressionType Type { get; }
    public int Width => Type.Width;
    public abstract string ToHlsl();
}

internal sealed class ConstantExpression : ExpressionNode
{
    public ConstantExpression(float value) : base(new ExpressionType(1))
    {
        Value = value;
    }

    public float Value { get; }
    public override string ToHlsl() => Value.ToString("R", CultureInfo.InvariantCulture);
}

internal sealed class LayerExpression : ExpressionNode
{
    public LayerExpression(int layerIndex) : base(new ExpressionType(4))
    {
        LayerIndex = layerIndex;
    }

    public int LayerIndex { get; }

    // The generated Compose() function receives every sampled layer as an array.
    public override string ToHlsl() => "layers[" + LayerIndex + "]";
}

internal sealed class SwizzleExpression : ExpressionNode
{
    public SwizzleExpression(ExpressionNode operand, string swizzle) : base(new ExpressionType(swizzle.Length))
    {
        Operand = operand;
        Swizzle = swizzle;
    }

    public ExpressionNode Operand { get; }
    public string Swizzle { get; }

    public override string ToHlsl()
    {
        // A layer read is a plain variable and can be swizzled directly; every other operand needs
        // parentheses so the swizzle binds to the whole expression.
        string source = Operand is LayerExpression ? Operand.ToHlsl() : "(" + Operand.ToHlsl() + ")";
        return source + "." + Swizzle;
    }
}

/// <summary>A call into <c>Shaders/Common.hlsl</c>: <c>a.lum</c> compiles to <c>lum(Layer0)</c>.</summary>
internal sealed class MemberExpression : ExpressionNode
{
    private readonly int[] _parameterWidths;

    public MemberExpression(string name, ExpressionNode operand, IReadOnlyList<ExpressionNode> arguments, MemberOverload overload)
        : base(new ExpressionType(overload.ResultWidth))
    {
        Name = name;
        Operand = operand;
        Arguments = arguments;
        _parameterWidths = overload.ParameterWidths;
    }

    public string Name { get; }
    public ExpressionNode Operand { get; }
    public IReadOnlyList<ExpressionNode> Arguments { get; }

    public override string ToHlsl()
    {
        var b = new StringBuilder(Name).Append('(').Append(Operand.ToHlsl());
        for (int i = 0; i < Arguments.Count; i++)
        {
            b.Append(", ");
            int width = _parameterWidths[i + 1];
            // A scalar argument of a vector parameter is splatted explicitly, so the HLSL overload is
            // chosen without relying on implicit scalar promotion.
            if (width > 1 && Arguments[i].Width == 1)
                b.Append("(float").Append(width).Append(")(").Append(Arguments[i].ToHlsl()).Append(')');
            else
                b.Append(Arguments[i].ToHlsl());
        }
        return b.Append(')').ToString();
    }
}

/// <summary>
/// The <c>,</c> operator: it concatenates the channels of its operands in order, exactly like the
/// reference engine's <c>|</c> operator. Chains are flattened, so <c>a.r, b.g, b.b</c> becomes one
/// <c>float3(...)</c> value.
/// </summary>
internal sealed class CompositionExpression : ExpressionNode
{
    public CompositionExpression(IReadOnlyList<ExpressionNode> operands, ExpressionType type) : base(type)
    {
        Operands = operands;
    }

    public IReadOnlyList<ExpressionNode> Operands { get; }

    public override string ToHlsl()
    {
        var b = new StringBuilder("float").Append(Width).Append('(');
        for (int i = 0; i < Operands.Count; i++)
        {
            if (i > 0) b.Append(", ");
            b.Append(Operands[i].ToHlsl());
        }
        return b.Append(')').ToString();
    }
}

internal sealed class UnaryExpression : ExpressionNode
{
    public UnaryExpression(char operation, ExpressionNode operand) : base(operand.Type)
    {
        Operation = operation;
        Operand = operand;
    }

    public char Operation { get; }
    public ExpressionNode Operand { get; }
    public override string ToHlsl() => "(" + Operation + Operand.ToHlsl() + ")";
}

internal sealed class BinaryExpression : ExpressionNode
{
    public BinaryExpression(char operation, ExpressionNode left, ExpressionNode right, ExpressionType type) : base(type)
    {
        Operation = operation;
        Left = left;
        Right = right;
    }

    public char Operation { get; }
    public ExpressionNode Left { get; }
    public ExpressionNode Right { get; }
    public override string ToHlsl() => "(" + Left.ToHlsl() + " " + Operation + " " + Right.ToHlsl() + ")";
}

internal static class ExpressionCompiler
{
    public static LayerCompose Compile(string expression, IReadOnlyList<string> layerNames)
    {
        if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentException("Expression cannot be empty.", nameof(expression));
        var parser = new ExpressionParser(expression, layerNames);
        ExpressionNode root = parser.Parse();
        return new LayerCompose(expression, root, parser.ReferencedLayers, parser.UsesMemberExpression);
    }

    private sealed class ExpressionParser
    {
        private readonly string _source;
        private readonly IReadOnlyList<string> _layerNames;
        private readonly ExpressionLexer _lexer;
        private ExpressionToken _current;

        public ExpressionParser(string source, IReadOnlyList<string> layerNames)
        {
            _source = source;
            _layerNames = layerNames;
            _lexer = new ExpressionLexer(source);
            _current = _lexer.Next();
            ReferencedLayers = new HashSet<int>();
        }

        public HashSet<int> ReferencedLayers { get; }

        public bool UsesMemberExpression { get; private set; }

        public ExpressionNode Parse()
        {
            ExpressionNode result = ParseExpression();
            Expect(ExpressionTokenKind.End);
            return result;
        }

        private ExpressionNode ParseExpression() => ParseComposition();

        /// <summary>
        /// The lowest precedence operator: <c>,</c> concatenates channels, just like the reference
        /// engine's <c>|</c>. Member arguments parse at the additive level, so a comma inside a call
        /// still separates arguments.
        /// </summary>
        private ExpressionNode ParseComposition()
        {
            ExpressionNode first = ParseAdditive();
            if (_current.Kind != ExpressionTokenKind.Comma) return first;

            var operands = new List<ExpressionNode> { first };
            int width = first.Width;
            while (Accept(ExpressionTokenKind.Comma))
            {
                ExpressionNode operand = ParseAdditive();
                width += operand.Width;
                if (width > 4)
                    throw Error("A composition can hold at most four channels, but " + width + " were combined.", _current);
                operands.Add(operand);
            }
            return new CompositionExpression(operands, new ExpressionType(width));
        }

        private ExpressionNode ParseAdditive()
        {
            ExpressionNode left = ParseMultiplicative();
            while (_current.Kind == ExpressionTokenKind.Plus || _current.Kind == ExpressionTokenKind.Minus)
            {
                char op = _current.Kind == ExpressionTokenKind.Plus ? '+' : '-';
                Advance();
                left = MakeBinary(op, left, ParseMultiplicative());
            }
            return left;
        }

        private ExpressionNode ParseMultiplicative()
        {
            ExpressionNode left = ParseUnary();
            while (_current.Kind == ExpressionTokenKind.Star || _current.Kind == ExpressionTokenKind.Slash)
            {
                char op = _current.Kind == ExpressionTokenKind.Star ? '*' : '/';
                Advance();
                left = MakeBinary(op, left, ParseUnary());
            }
            return left;
        }

        private ExpressionNode ParseUnary()
        {
            if (_current.Kind == ExpressionTokenKind.Plus || _current.Kind == ExpressionTokenKind.Minus)
            {
                char op = _current.Kind == ExpressionTokenKind.Plus ? '+' : '-';
                Advance();
                return new UnaryExpression(op, ParseUnary());
            }
            return ParsePrimary();
        }

        private ExpressionNode ParsePrimary()
        {
            if (Accept(ExpressionTokenKind.LeftParen))
            {
                ExpressionNode expression = ParseExpression();
                ExpressionToken closing = Expect(ExpressionTokenKind.RightParen);
                return ParseAccessors(expression, closing);
            }

            if (_current.Kind == ExpressionTokenKind.Number)
            {
                ExpressionToken token = _current;
                Advance();
                float value;
                if (!float.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    throw Error("Invalid numeric constant.", token);
                return new ConstantExpression(value);
            }

            return ParseReference();
        }

        private ExpressionNode ParseReference()
        {
            ExpressionToken layerToken = Expect(ExpressionTokenKind.Identifier);
            int layerIndex = -1;
            for (int i = 0; i < _layerNames.Count; i++)
                if (string.Equals(_layerNames[i], layerToken.Text, StringComparison.Ordinal)) { layerIndex = i; break; }
            if (layerIndex < 0)
            {
                if (ShaderLibrary.TryGetMember(layerToken.Text, out _))
                    throw Error("'" + layerToken.Text + "' is a shader member and must be applied to a layer, for example 'a." + layerToken.Text + "'.", layerToken);
                throw Error("Unknown layer '" + layerToken.Text + "'.", layerToken);
            }
            ReferencedLayers.Add(layerIndex);
            // A bare layer reference is the whole layer, like the reference engine's bare source name.
            return ParseAccessors(new LayerExpression(layerIndex), layerToken);
        }

        /// <summary>
        /// Reads the <c>.name</c> chain that follows a layer or a parenthesized term. Every step is either
        /// a channel swizzle or a member function of <c>Shaders/Common.hlsl</c>, optionally with arguments.
        /// </summary>
        private ExpressionNode ParseAccessors(ExpressionNode operand, ExpressionToken ownerToken)
        {
            while (_current.Kind == ExpressionTokenKind.Dot)
            {
                Advance();
                ExpressionToken nameToken = Expect(ExpressionTokenKind.Identifier);
                bool call = _current.Kind == ExpressionTokenKind.LeftParen;
                List<MemberOverload> overloads;
                if (!call && IsSwizzle(nameToken.Text))
                {
                    operand = MakeSwizzle(operand, nameToken);
                }
                else if (ShaderLibrary.TryGetMember(nameToken.Text, out overloads))
                {
                    operand = MakeMember(operand, nameToken, overloads, call ? ParseArguments() : new List<ExpressionNode>());
                    // Intrinsics are HLSL builtins, so only a real library function needs Common.hlsl.
                    if (!ShaderLibrary.IsIntrinsic(nameToken.Text)) UsesMemberExpression = true;
                }
                else if (call)
                {
                    throw Error("Unknown member '" + nameToken.Text + "'. Available members: " + string.Join(", ", ShaderLibrary.MemberNames.ToArray()) + ".", nameToken);
                }
                else
                {
                    throw Error("Unknown member or channel swizzle '" + nameToken.Text + "'. Available members: " + string.Join(", ", ShaderLibrary.MemberNames.ToArray()) + ".", nameToken);
                }
            }
            return operand;
        }

        private List<ExpressionNode> ParseArguments()
        {
            Expect(ExpressionTokenKind.LeftParen);
            var arguments = new List<ExpressionNode>();
            if (Accept(ExpressionTokenKind.RightParen)) return arguments;
            // Additive, not expression: a comma here separates arguments instead of composing channels.
            arguments.Add(ParseAdditive());
            while (Accept(ExpressionTokenKind.Comma)) arguments.Add(ParseAdditive());
            Expect(ExpressionTokenKind.RightParen);
            return arguments;
        }

        /// <summary>
        /// Any combination of <c>r</c>, <c>g</c>, <c>b</c> and <c>a</c> up to four channels, matching the
        /// swizzles the reference engine accepts (for example <c>bgr</c> or <c>rr</c>).
        /// </summary>
        private static bool IsSwizzle(string name)
        {
            if (name.Length == 0 || name.Length > 4) return false;
            for (int i = 0; i < name.Length; i++)
                if ("rgba".IndexOf(name[i]) < 0) return false;
            return true;
        }

        private ExpressionNode MakeSwizzle(ExpressionNode operand, ExpressionToken token)
        {
            for (int i = 0; i < token.Text.Length; i++)
            {
                if ("rgba".IndexOf(token.Text[i]) >= operand.Width)
                    throw Error("Channel '" + token.Text[i] + "' is not part of a " + operand.Width + " channel value.", token);
            }
            return new SwizzleExpression(operand, token.Text);
        }

        private ExpressionNode MakeMember(ExpressionNode operand, ExpressionToken token, List<MemberOverload> overloads, List<ExpressionNode> arguments)
        {
            MemberOverload? selected = null;
            bool selectedExactly = false;
            foreach (MemberOverload overload in overloads)
            {
                if (overload.ArgumentCount != arguments.Count) continue;
                if (overload.OperandWidth != operand.Width) continue;
                bool exactly = true;
                bool compatible = true;
                for (int i = 0; i < arguments.Count; i++)
                {
                    int parameterWidth = overload.ParameterWidths[i + 1];
                    if (arguments[i].Width == parameterWidth) continue;
                    if (arguments[i].Width == 1) { exactly = false; continue; }
                    compatible = false;
                    break;
                }
                if (!compatible) continue;
                if (selected == null || (exactly && !selectedExactly))
                {
                    selected = overload;
                    selectedExactly = exactly;
                }
            }

            if (selected == null)
            {
                int[] widths = overloads.Where(x => x.ArgumentCount == arguments.Count).Select(x => x.OperandWidth).Distinct().OrderBy(x => x).ToArray();
                string reason = widths.Length == 0
                    ? "does not take " + arguments.Count + " argument" + (arguments.Count == 1 ? string.Empty : "s")
                    : "does not accept a " + operand.Width + " channel operand";
                throw Error("Member '" + token.Text + "' " + reason + ". Overloads: " + ShaderLibrary.DescribeOverloads(overloads) + ".", token);
            }
            return new MemberExpression(token.Text, operand, arguments, selected.Value);
        }

        private static ExpressionNode MakeBinary(char operation, ExpressionNode left, ExpressionNode right)
        {
            if (left.Width != right.Width && left.Width != 1 && right.Width != 1)
                throw new FormatException("Vector operands must have the same width; scalar broadcasting is the only implicit conversion.");
            return new BinaryExpression(operation, left, right, new ExpressionType(Math.Max(left.Width, right.Width)));
        }

        private ExpressionToken Expect(ExpressionTokenKind kind)
        {
            if (_current.Kind != kind) throw Error("Expected " + kind + ".", _current);
            ExpressionToken result = _current;
            Advance();
            return result;
        }

        private bool Accept(ExpressionTokenKind kind)
        {
            if (_current.Kind != kind) return false;
            Advance();
            return true;
        }

        private void Advance() => _current = _lexer.Next();

        private FormatException Error(string message, ExpressionToken token) => new FormatException(message + " Position: " + token.Position + ".");
    }
}
