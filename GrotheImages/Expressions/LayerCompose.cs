using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GrotheImages;

public sealed class LayerCompose
{
    internal LayerCompose(string expression, IReadOnlyList<ExpressionNode> outputs, IReadOnlyCollection<int> referencedLayers)
    {
        Expression = expression;
        Outputs = new ReadOnlyCollection<ExpressionNode>(outputs.ToList());
        ReferencedLayerIndices = new ReadOnlyCollection<int>(referencedLayers.ToList());
        OutputChannelCount = outputs.Sum(x => x.Width);
    }

    public string Expression { get; }
    public int OutputChannelCount { get; }

    internal ReadOnlyCollection<ExpressionNode> Outputs { get; }
    internal ReadOnlyCollection<int> ReferencedLayerIndices { get; }

    internal string ToHlsl()
    {
        var values = Outputs.Select(x => x.ToHlsl()).ToArray();
        return string.Join(", ", values);
    }
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

internal sealed class ChannelExpression : ExpressionNode
{
    public ChannelExpression(int layerIndex, string swizzle) : base(new ExpressionType(swizzle.Length))
    {
        LayerIndex = layerIndex;
        Swizzle = swizzle;
    }

    public int LayerIndex { get; }
    public string Swizzle { get; }
    public override string ToHlsl() => "Layer" + LayerIndex + "." + Swizzle;
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
    private static readonly HashSet<string> Swizzles = new HashSet<string>(StringComparer.Ordinal)
    {
        "r", "g", "b", "a", "rg", "gb", "rgb", "rgba"
    };

    public static LayerCompose Compile(string expression, IReadOnlyList<string> layerNames)
    {
        if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentException("Expression cannot be empty.", nameof(expression));
        var parser = new ExpressionParser(expression, layerNames);
        var outputs = parser.Parse();
        int channels = outputs.Sum(x => x.Width);
        if (channels < 1 || channels > 4)
            throw new FormatException("The expression must produce between one and four channels.");
        return new LayerCompose(expression, outputs, parser.ReferencedLayers);
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

        public List<ExpressionNode> Parse()
        {
            var result = new List<ExpressionNode> { ParseExpression() };
            while (Accept(ExpressionTokenKind.Comma)) result.Add(ParseExpression());
            Expect(ExpressionTokenKind.End);
            return result;
        }

        private ExpressionNode ParseExpression() => ParseAdditive();

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
                Expect(ExpressionTokenKind.RightParen);
                return expression;
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

            ExpressionToken layerToken = Expect(ExpressionTokenKind.Identifier);
            Expect(ExpressionTokenKind.Dot);
            ExpressionToken swizzleToken = Expect(ExpressionTokenKind.Identifier);
            if (!Swizzles.Contains(swizzleToken.Text)) throw Error("Unsupported channel swizzle.", swizzleToken);
            int layerIndex = -1;
            for (int i = 0; i < _layerNames.Count; i++)
                if (string.Equals(_layerNames[i], layerToken.Text, StringComparison.Ordinal)) { layerIndex = i; break; }
            if (layerIndex < 0) throw Error("Unknown layer '" + layerToken.Text + "'.", layerToken);
            ReferencedLayers.Add(layerIndex);
            return new ChannelExpression(layerIndex, swizzleToken.Text);
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
