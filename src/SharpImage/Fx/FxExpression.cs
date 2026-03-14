// SharpImage — FX expression language: lexer, parser, and AST nodes.
// Supports per-pixel math expressions inspired by ImageMagick -fx.

using System.Globalization;
using System.Runtime.CompilerServices;

namespace SharpImage.Fx;

// ══════════════════════════════════════════════════════════════
//  Token types
// ══════════════════════════════════════════════════════════════

internal enum TokenKind
{
    Number,
    Identifier,
    Plus, Minus, Star, Slash, Percent, Caret,
    LeftParen, RightParen,
    LeftBracket, RightBracket,
    Comma, Dot, Semicolon,
    Equal, NotEqual,
    Less, LessEqual, Greater, GreaterEqual,
    And, Or, Not,
    Question, Colon,
    End
}

internal readonly struct Token
{
    public readonly TokenKind Kind;
    public readonly double NumberValue;
    public readonly string TextValue;
    public readonly int Position;

    public Token(TokenKind kind, int position, double numberValue = 0, string textValue = "")
    {
        Kind = kind;
        Position = position;
        NumberValue = numberValue;
        TextValue = textValue;
    }
}

// ══════════════════════════════════════════════════════════════
//  Lexer
// ══════════════════════════════════════════════════════════════

internal sealed class FxLexer
{
    private readonly string _source;
    private int _pos;

    public FxLexer(string source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _pos = 0;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (_pos < _source.Length)
        {
            char c = _source[_pos];

            if (char.IsWhiteSpace(c)) { _pos++; continue; }

            if (c == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '/')
            {
                // Line comment
                while (_pos < _source.Length && _source[_pos] != '\n') _pos++;
                continue;
            }

            int start = _pos;

            if (char.IsDigit(c) || (c == '.' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1])))
            {
                tokens.Add(ReadNumber(start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                tokens.Add(ReadIdentifier(start));
                continue;
            }

            switch (c)
            {
                case '+': tokens.Add(new Token(TokenKind.Plus, start)); _pos++; break;
                case '-': tokens.Add(new Token(TokenKind.Minus, start)); _pos++; break;
                case '*': tokens.Add(new Token(TokenKind.Star, start)); _pos++; break;
                case '/': tokens.Add(new Token(TokenKind.Slash, start)); _pos++; break;
                case '%': tokens.Add(new Token(TokenKind.Percent, start)); _pos++; break;
                case '^': tokens.Add(new Token(TokenKind.Caret, start)); _pos++; break;
                case '(': tokens.Add(new Token(TokenKind.LeftParen, start)); _pos++; break;
                case ')': tokens.Add(new Token(TokenKind.RightParen, start)); _pos++; break;
                case '[': tokens.Add(new Token(TokenKind.LeftBracket, start)); _pos++; break;
                case ']': tokens.Add(new Token(TokenKind.RightBracket, start)); _pos++; break;
                case ',': tokens.Add(new Token(TokenKind.Comma, start)); _pos++; break;
                case '.': tokens.Add(new Token(TokenKind.Dot, start)); _pos++; break;
                case ';': tokens.Add(new Token(TokenKind.Semicolon, start)); _pos++; break;
                case '?': tokens.Add(new Token(TokenKind.Question, start)); _pos++; break;
                case ':': tokens.Add(new Token(TokenKind.Colon, start)); _pos++; break;

                case '=':
                    _pos++;
                    if (_pos < _source.Length && _source[_pos] == '=') { tokens.Add(new Token(TokenKind.Equal, start)); _pos++; }
                    else throw new FxParseException($"Unexpected '=' at position {start}. Use '==' for comparison.");
                    break;
                case '!':
                    _pos++;
                    if (_pos < _source.Length && _source[_pos] == '=') { tokens.Add(new Token(TokenKind.NotEqual, start)); _pos++; }
                    else tokens.Add(new Token(TokenKind.Not, start));
                    break;
                case '<':
                    _pos++;
                    if (_pos < _source.Length && _source[_pos] == '=') { tokens.Add(new Token(TokenKind.LessEqual, start)); _pos++; }
                    else tokens.Add(new Token(TokenKind.Less, start));
                    break;
                case '>':
                    _pos++;
                    if (_pos < _source.Length && _source[_pos] == '=') { tokens.Add(new Token(TokenKind.GreaterEqual, start)); _pos++; }
                    else tokens.Add(new Token(TokenKind.Greater, start));
                    break;
                case '&':
                    _pos++;
                    if (_pos < _source.Length && _source[_pos] == '&') { tokens.Add(new Token(TokenKind.And, start)); _pos++; }
                    else throw new FxParseException($"Unexpected '&' at position {start}. Use '&&' for logical AND.");
                    break;
                case '|':
                    _pos++;
                    if (_pos < _source.Length && _source[_pos] == '|') { tokens.Add(new Token(TokenKind.Or, start)); _pos++; }
                    else throw new FxParseException($"Unexpected '|' at position {start}. Use '||' for logical OR.");
                    break;

                default:
                    throw new FxParseException($"Unexpected character '{c}' at position {start}.");
            }
        }

        tokens.Add(new Token(TokenKind.End, _pos));
        return tokens;
    }

    private Token ReadNumber(int start)
    {
        while (_pos < _source.Length && (char.IsDigit(_source[_pos]) || _source[_pos] == '.'))
            _pos++;
        // Handle scientific notation
        if (_pos < _source.Length && (_source[_pos] == 'e' || _source[_pos] == 'E'))
        {
            _pos++;
            if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-')) _pos++;
            while (_pos < _source.Length && char.IsDigit(_source[_pos])) _pos++;
        }

        string text = _source[start.._pos];
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new FxParseException($"Invalid number '{text}' at position {start}.");
        return new Token(TokenKind.Number, start, value);
    }

    private Token ReadIdentifier(int start)
    {
        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
            _pos++;
        string text = _source[start.._pos];
        return new Token(TokenKind.Identifier, start, textValue: text);
    }
}

// ══════════════════════════════════════════════════════════════
//  AST Nodes
// ══════════════════════════════════════════════════════════════

internal abstract class FxNode
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public abstract double Evaluate(ref FxContext ctx);
}

internal sealed class NumberNode : FxNode
{
    public readonly double Value;
    public NumberNode(double value) => Value = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override double Evaluate(ref FxContext ctx) => Value;
}

internal sealed class UnaryNode : FxNode
{
    public readonly TokenKind Op;
    public readonly FxNode Operand;
    public UnaryNode(TokenKind op, FxNode operand) { Op = op; Operand = operand; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override double Evaluate(ref FxContext ctx)
    {
        double v = Operand.Evaluate(ref ctx);
        return Op switch
        {
            TokenKind.Minus => -v,
            TokenKind.Plus => v,
            TokenKind.Not => v == 0 ? 1.0 : 0.0,
            _ => v
        };
    }
}

internal sealed class BinaryNode : FxNode
{
    public readonly TokenKind Op;
    public readonly FxNode Left, Right;
    public BinaryNode(TokenKind op, FxNode left, FxNode right)
    {
        Op = op; Left = left; Right = right;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override double Evaluate(ref FxContext ctx)
    {
        double l = Left.Evaluate(ref ctx);
        // Short-circuit for logical ops
        if (Op == TokenKind.And) return l != 0 && Right.Evaluate(ref ctx) != 0 ? 1.0 : 0.0;
        if (Op == TokenKind.Or) return l != 0 ? 1.0 : Right.Evaluate(ref ctx) != 0 ? 1.0 : 0.0;

        double r = Right.Evaluate(ref ctx);
        return Op switch
        {
            TokenKind.Plus => l + r,
            TokenKind.Minus => l - r,
            TokenKind.Star => l * r,
            TokenKind.Slash => r == 0 ? 0.0 : l / r,
            TokenKind.Percent => r == 0 ? 0.0 : l % r,
            TokenKind.Caret => Math.Pow(l, r),
            TokenKind.Equal => l == r ? 1.0 : 0.0,
            TokenKind.NotEqual => l != r ? 1.0 : 0.0,
            TokenKind.Less => l < r ? 1.0 : 0.0,
            TokenKind.LessEqual => l <= r ? 1.0 : 0.0,
            TokenKind.Greater => l > r ? 1.0 : 0.0,
            TokenKind.GreaterEqual => l >= r ? 1.0 : 0.0,
            _ => 0.0
        };
    }
}

internal sealed class TernaryNode : FxNode
{
    public readonly FxNode Condition, TrueExpr, FalseExpr;
    public TernaryNode(FxNode condition, FxNode trueExpr, FxNode falseExpr)
    {
        Condition = condition; TrueExpr = trueExpr; FalseExpr = falseExpr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override double Evaluate(ref FxContext ctx)
        => Condition.Evaluate(ref ctx) != 0 ? TrueExpr.Evaluate(ref ctx) : FalseExpr.Evaluate(ref ctx);
}

internal sealed class SequenceNode : FxNode
{
    public readonly FxNode[] Expressions;
    public SequenceNode(FxNode[] expressions) => Expressions = expressions;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override double Evaluate(ref FxContext ctx)
    {
        double result = 0;
        for (int i = 0; i < Expressions.Length; i++)
            result = Expressions[i].Evaluate(ref ctx);
        return result;
    }
}

// Pixel channel accessor: u, u.r, u.g, u.b, u.a, u[n], u[n].r
internal sealed class PixelAccessNode : FxNode
{
    public readonly int ImageIndex; // -1 = current
    public readonly FxNode? ImageIndexExpr; // For dynamic u[expr]
    public readonly ChannelSelector Channel;

    public PixelAccessNode(int imageIndex, ChannelSelector channel, FxNode? imageIndexExpr = null)
    {
        ImageIndex = imageIndex; Channel = channel; ImageIndexExpr = imageIndexExpr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override double Evaluate(ref FxContext ctx)
    {
        int imgIdx = ImageIndexExpr != null
            ? (int)ImageIndexExpr.Evaluate(ref ctx)
            : (ImageIndex >= 0 ? ImageIndex : ctx.CurrentImageIndex);
        return ctx.GetPixelChannel(imgIdx, Channel);
    }
}

internal enum ChannelSelector
{
    Default, // Returns the "current channel" value (for per-channel mode) or luma
    Red, Green, Blue, Alpha,
    Hue, Saturation, Lightness,
    Intensity
}

// Variable: i, j, w, h, n, z
internal sealed class VariableNode : FxNode
{
    public readonly VariableKind Kind;
    public VariableNode(VariableKind kind) => Kind = kind;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override double Evaluate(ref FxContext ctx)
    {
        return Kind switch
        {
            VariableKind.I => ctx.X,
            VariableKind.J => ctx.Y,
            VariableKind.W => ctx.Width,
            VariableKind.H => ctx.Height,
            VariableKind.N => ctx.CurrentImageIndex,
            _ => 0.0
        };
    }
}

internal enum VariableKind { I, J, W, H, N }

// Built-in function call: sin(x), max(a,b), clamp(v,lo,hi), etc.
internal sealed class FunctionCallNode : FxNode
{
    public readonly FxBuiltinFunc Func;
    public readonly FxNode[] Args;
    public FunctionCallNode(FxBuiltinFunc func, FxNode[] args) { Func = func; Args = args; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override double Evaluate(ref FxContext ctx)
    {
        return Func switch
        {
            // 1-arg
            FxBuiltinFunc.Sin => Math.Sin(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Cos => Math.Cos(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Tan => Math.Tan(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Asin => Math.Asin(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Acos => Math.Acos(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Atan => Math.Atan(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Sinh => Math.Sinh(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Cosh => Math.Cosh(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Tanh => Math.Tanh(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Abs => Math.Abs(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Ceil => Math.Ceiling(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Floor => Math.Floor(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Round => Math.Round(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Trunc => Math.Truncate(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Sqrt => Math.Sqrt(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Exp => Math.Exp(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Ln => Math.Log(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Log => Math.Log10(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Log2 => Math.Log2(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Sign => Math.Sign(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Not => Args[0].Evaluate(ref ctx) == 0 ? 1.0 : 0.0,
            FxBuiltinFunc.Sinc => EvalSinc(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Gauss => EvalGauss(Args[0].Evaluate(ref ctx)),
            FxBuiltinFunc.Int => (double)(int)Args[0].Evaluate(ref ctx),
            FxBuiltinFunc.IsNaN => double.IsNaN(Args[0].Evaluate(ref ctx)) ? 1.0 : 0.0,
            // 2-arg
            FxBuiltinFunc.Atan2 => Math.Atan2(Args[0].Evaluate(ref ctx), Args[1].Evaluate(ref ctx)),
            FxBuiltinFunc.Pow => Math.Pow(Args[0].Evaluate(ref ctx), Args[1].Evaluate(ref ctx)),
            FxBuiltinFunc.Min => Math.Min(Args[0].Evaluate(ref ctx), Args[1].Evaluate(ref ctx)),
            FxBuiltinFunc.Max => Math.Max(Args[0].Evaluate(ref ctx), Args[1].Evaluate(ref ctx)),
            FxBuiltinFunc.Mod => EvalMod(Args[0].Evaluate(ref ctx), Args[1].Evaluate(ref ctx)),
            FxBuiltinFunc.Hypot => Math.Sqrt(
                Args[0].Evaluate(ref ctx) * Args[0].Evaluate(ref ctx) +
                Args[1].Evaluate(ref ctx) * Args[1].Evaluate(ref ctx)),
            // 3-arg
            FxBuiltinFunc.Clamp => Math.Clamp(Args[0].Evaluate(ref ctx),
                Args[1].Evaluate(ref ctx), Args[2].Evaluate(ref ctx)),
            FxBuiltinFunc.If => Args[0].Evaluate(ref ctx) != 0
                ? Args[1].Evaluate(ref ctx) : Args[2].Evaluate(ref ctx),
            // Pixel lookup: p(x,y) — read pixel at (x,y) from current image
            FxBuiltinFunc.P => ctx.GetPixelAt(
                (int)Args[0].Evaluate(ref ctx), (int)Args[1].Evaluate(ref ctx), ChannelSelector.Default),
            _ => 0.0
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double EvalSinc(double x)
    {
        if (x == 0) return 1.0;
        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double EvalGauss(double x) => Math.Exp(-x * x / 2.0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double EvalMod(double a, double b) => b == 0 ? 0.0 : a % b;
}

internal enum FxBuiltinFunc
{
    Sin, Cos, Tan, Asin, Acos, Atan, Atan2,
    Sinh, Cosh, Tanh,
    Abs, Ceil, Floor, Round, Trunc, Int,
    Sqrt, Exp, Ln, Log, Log2, Pow,
    Min, Max, Mod, Hypot,
    Sign, Not, Sinc, Gauss, IsNaN,
    Clamp, If, P
}

// ══════════════════════════════════════════════════════════════
//  Evaluation context (per-pixel state, passed by ref for perf)
// ══════════════════════════════════════════════════════════════

internal struct FxContext
{
    public int X, Y;
    public int Width, Height;
    public int CurrentImageIndex;
    public int ChannelCount;
    public bool HasAlpha;

    // Source image pixel data — jagged array: [imageIndex][y * width * channels + x * channels + c]
    private readonly double[][] _normalizedPixels;
    private readonly int[] _widths;
    private readonly int[] _heights;
    private readonly int[] _channelCounts;

    public FxContext(double[][] normalizedPixels, int[] widths, int[] heights, int[] channelCounts,
        bool hasAlpha)
    {
        _normalizedPixels = normalizedPixels;
        _widths = widths;
        _heights = heights;
        _channelCounts = channelCounts;
        HasAlpha = hasAlpha;
        Width = widths[0];
        Height = heights[0];
        ChannelCount = channelCounts[0];
        X = 0; Y = 0;
        CurrentImageIndex = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly double GetPixelChannel(int imageIndex, ChannelSelector channel)
    {
        if (imageIndex < 0 || imageIndex >= _normalizedPixels.Length) return 0.0;
        return GetPixelAtImpl(imageIndex, X, Y, channel);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly double GetPixelAt(int x, int y, ChannelSelector channel)
    {
        return GetPixelAtImpl(CurrentImageIndex, x, y, channel);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly double GetPixelAtImpl(int imageIndex, int x, int y, ChannelSelector channel)
    {
        int w = _widths[imageIndex];
        int h = _heights[imageIndex];
        int ch = _channelCounts[imageIndex];

        // Clamp coordinates
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);

        int baseIdx = (y * w + x) * ch;
        double[] pixels = _normalizedPixels[imageIndex];

        return channel switch
        {
            ChannelSelector.Red => pixels[baseIdx],
            ChannelSelector.Green => ch >= 3 ? pixels[baseIdx + 1] : pixels[baseIdx],
            ChannelSelector.Blue => ch >= 3 ? pixels[baseIdx + 2] : pixels[baseIdx],
            ChannelSelector.Alpha => HasAlpha && ch >= 4 ? pixels[baseIdx + ch - 1] : 1.0,
            ChannelSelector.Intensity => ch >= 3
                ? 0.212656 * pixels[baseIdx] + 0.715158 * pixels[baseIdx + 1] + 0.072186 * pixels[baseIdx + 2]
                : pixels[baseIdx],
            ChannelSelector.Default => ch >= 3
                ? 0.212656 * pixels[baseIdx] + 0.715158 * pixels[baseIdx + 1] + 0.072186 * pixels[baseIdx + 2]
                : pixels[baseIdx],
            // HSL conversions
            ChannelSelector.Hue => ComputeHue(pixels, baseIdx, ch),
            ChannelSelector.Saturation => ComputeSaturation(pixels, baseIdx, ch),
            ChannelSelector.Lightness => ComputeLightness(pixels, baseIdx, ch),
            _ => pixels[baseIdx]
        };
    }

    private static double ComputeHue(double[] pixels, int baseIdx, int ch)
    {
        if (ch < 3) return 0.0;
        double r = pixels[baseIdx], g = pixels[baseIdx + 1], b = pixels[baseIdx + 2];
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        if (delta < 1e-10) return 0.0;
        double h;
        if (max == r) h = (g - b) / delta;
        else if (max == g) h = 2.0 + (b - r) / delta;
        else h = 4.0 + (r - g) / delta;
        h /= 6.0;
        if (h < 0) h += 1.0;
        return h;
    }

    private static double ComputeSaturation(double[] pixels, int baseIdx, int ch)
    {
        if (ch < 3) return 0.0;
        double r = pixels[baseIdx], g = pixels[baseIdx + 1], b = pixels[baseIdx + 2];
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;
        if (max == min) return 0.0;
        return l > 0.5 ? (max - min) / (2.0 - max - min) : (max - min) / (max + min);
    }

    private static double ComputeLightness(double[] pixels, int baseIdx, int ch)
    {
        if (ch < 3) return pixels[baseIdx];
        double max = Math.Max(pixels[baseIdx], Math.Max(pixels[baseIdx + 1], pixels[baseIdx + 2]));
        double min = Math.Min(pixels[baseIdx], Math.Min(pixels[baseIdx + 1], pixels[baseIdx + 2]));
        return (max + min) / 2.0;
    }
}

// ══════════════════════════════════════════════════════════════
//  Parser — recursive descent
// ══════════════════════════════════════════════════════════════

internal sealed class FxParser
{
    private readonly List<Token> _tokens;
    private int _pos;

    // Known functions
    private static readonly Dictionary<string, (FxBuiltinFunc func, int argCount)> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sin"] = (FxBuiltinFunc.Sin, 1),
        ["cos"] = (FxBuiltinFunc.Cos, 1),
        ["tan"] = (FxBuiltinFunc.Tan, 1),
        ["asin"] = (FxBuiltinFunc.Asin, 1),
        ["acos"] = (FxBuiltinFunc.Acos, 1),
        ["atan"] = (FxBuiltinFunc.Atan, 1),
        ["atan2"] = (FxBuiltinFunc.Atan2, 2),
        ["sinh"] = (FxBuiltinFunc.Sinh, 1),
        ["cosh"] = (FxBuiltinFunc.Cosh, 1),
        ["tanh"] = (FxBuiltinFunc.Tanh, 1),
        ["abs"] = (FxBuiltinFunc.Abs, 1),
        ["ceil"] = (FxBuiltinFunc.Ceil, 1),
        ["floor"] = (FxBuiltinFunc.Floor, 1),
        ["round"] = (FxBuiltinFunc.Round, 1),
        ["trunc"] = (FxBuiltinFunc.Trunc, 1),
        ["int"] = (FxBuiltinFunc.Int, 1),
        ["sqrt"] = (FxBuiltinFunc.Sqrt, 1),
        ["exp"] = (FxBuiltinFunc.Exp, 1),
        ["ln"] = (FxBuiltinFunc.Ln, 1),
        ["log"] = (FxBuiltinFunc.Log, 1),
        ["logtwo"] = (FxBuiltinFunc.Log2, 1),
        ["log2"] = (FxBuiltinFunc.Log2, 1),
        ["pow"] = (FxBuiltinFunc.Pow, 2),
        ["min"] = (FxBuiltinFunc.Min, 2),
        ["max"] = (FxBuiltinFunc.Max, 2),
        ["mod"] = (FxBuiltinFunc.Mod, 2),
        ["hypot"] = (FxBuiltinFunc.Hypot, 2),
        ["sign"] = (FxBuiltinFunc.Sign, 1),
        ["not"] = (FxBuiltinFunc.Not, 1),
        ["sinc"] = (FxBuiltinFunc.Sinc, 1),
        ["gauss"] = (FxBuiltinFunc.Gauss, 1),
        ["isnan"] = (FxBuiltinFunc.IsNaN, 1),
        ["clamp"] = (FxBuiltinFunc.Clamp, 3),
        ["if"] = (FxBuiltinFunc.If, 3),
        ["p"] = (FxBuiltinFunc.P, 2),
    };

    // Known constants
    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI,
        ["e"] = Math.E,
        ["phi"] = (1.0 + Math.Sqrt(5.0)) / 2.0,
        ["epsilon"] = double.Epsilon,
        ["quantumrange"] = 65535.0,
        ["quantumscale"] = 1.0 / 65535.0,
        ["maxrgb"] = 65535.0,
        ["opaque"] = 1.0,
        ["transparent"] = 0.0,
    };

    // Known variables (not pixel access)
    private static readonly Dictionary<string, VariableKind> Variables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["i"] = VariableKind.I,
        ["j"] = VariableKind.J,
        ["x"] = VariableKind.I,
        ["y"] = VariableKind.J,
        ["w"] = VariableKind.W,
        ["h"] = VariableKind.H,
        ["width"] = VariableKind.W,
        ["height"] = VariableKind.H,
        ["n"] = VariableKind.N,
    };

    // Channel name map
    private static readonly Dictionary<string, ChannelSelector> ChannelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["r"] = ChannelSelector.Red,
        ["red"] = ChannelSelector.Red,
        ["g"] = ChannelSelector.Green,
        ["green"] = ChannelSelector.Green,
        ["b"] = ChannelSelector.Blue,
        ["blue"] = ChannelSelector.Blue,
        ["a"] = ChannelSelector.Alpha,
        ["o"] = ChannelSelector.Alpha,
        ["alpha"] = ChannelSelector.Alpha,
        ["opacity"] = ChannelSelector.Alpha,
        ["hue"] = ChannelSelector.Hue,
        ["saturation"] = ChannelSelector.Saturation,
        ["lightness"] = ChannelSelector.Lightness,
        ["intensity"] = ChannelSelector.Intensity,
    };

    public FxParser(List<Token> tokens) => _tokens = tokens;

    public FxNode Parse()
    {
        var node = ParseSequence();
        if (Current.Kind != TokenKind.End)
            throw new FxParseException($"Unexpected token '{Current.Kind}' at position {Current.Position}.");
        return node;
    }

    private Token Current => _tokens[_pos];

    private Token Advance()
    {
        var t = _tokens[_pos];
        if (_pos < _tokens.Count - 1) _pos++;
        return t;
    }

    private void Expect(TokenKind kind)
    {
        if (Current.Kind != kind)
            throw new FxParseException($"Expected {kind} but got {Current.Kind} at position {Current.Position}.");
        Advance();
    }

    // Sequence: expr (';' expr)*
    private FxNode ParseSequence()
    {
        var exprs = new List<FxNode> { ParseTernary() };
        while (Current.Kind == TokenKind.Semicolon)
        {
            Advance();
            if (Current.Kind == TokenKind.End) break;
            exprs.Add(ParseTernary());
        }
        return exprs.Count == 1 ? exprs[0] : new SequenceNode(exprs.ToArray());
    }

    // Ternary: logicalOr ('?' expr ':' expr)?
    private FxNode ParseTernary()
    {
        var node = ParseLogicalOr();
        if (Current.Kind == TokenKind.Question)
        {
            Advance();
            var trueExpr = ParseTernary();
            Expect(TokenKind.Colon);
            var falseExpr = ParseTernary();
            node = new TernaryNode(node, trueExpr, falseExpr);
        }
        return node;
    }

    private FxNode ParseLogicalOr()
    {
        var node = ParseLogicalAnd();
        while (Current.Kind == TokenKind.Or)
        {
            Advance();
            node = new BinaryNode(TokenKind.Or, node, ParseLogicalAnd());
        }
        return node;
    }

    private FxNode ParseLogicalAnd()
    {
        var node = ParseComparison();
        while (Current.Kind == TokenKind.And)
        {
            Advance();
            node = new BinaryNode(TokenKind.And, node, ParseComparison());
        }
        return node;
    }

    private FxNode ParseComparison()
    {
        var node = ParseAdditive();
        while (Current.Kind is TokenKind.Equal or TokenKind.NotEqual or
               TokenKind.Less or TokenKind.LessEqual or
               TokenKind.Greater or TokenKind.GreaterEqual)
        {
            var op = Advance().Kind;
            node = new BinaryNode(op, node, ParseAdditive());
        }
        return node;
    }

    private FxNode ParseAdditive()
    {
        var node = ParseMultiplicative();
        while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var op = Advance().Kind;
            node = new BinaryNode(op, node, ParseMultiplicative());
        }
        return node;
    }

    private FxNode ParseMultiplicative()
    {
        var node = ParsePower();
        while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
        {
            var op = Advance().Kind;
            node = new BinaryNode(op, node, ParsePower());
        }
        return node;
    }

    // Right-associative
    private FxNode ParsePower()
    {
        var node = ParseUnary();
        if (Current.Kind == TokenKind.Caret)
        {
            Advance();
            node = new BinaryNode(TokenKind.Caret, node, ParsePower());
        }
        return node;
    }

    private FxNode ParseUnary()
    {
        if (Current.Kind is TokenKind.Minus or TokenKind.Plus or TokenKind.Not)
        {
            var op = Advance().Kind;
            return new UnaryNode(op, ParseUnary());
        }
        return ParsePostfix();
    }

    private FxNode ParsePostfix()
    {
        var node = ParsePrimary();
        // Handle .channel access on pixel nodes
        while (Current.Kind == TokenKind.Dot)
        {
            Advance();
            if (Current.Kind != TokenKind.Identifier)
                throw new FxParseException($"Expected channel name after '.' at position {Current.Position}.");
            string channelName = Advance().TextValue;
            if (!ChannelNames.TryGetValue(channelName, out var channel))
                throw new FxParseException($"Unknown channel '{channelName}'.");

            if (node is PixelAccessNode pan)
                node = new PixelAccessNode(pan.ImageIndex, channel, pan.ImageIndexExpr);
            else
                throw new FxParseException($"Channel accessor '.{channelName}' can only be applied to pixel references (u, v, s).");
        }
        return node;
    }

    private FxNode ParsePrimary()
    {
        var token = Current;

        switch (token.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new NumberNode(token.NumberValue);

            case TokenKind.LeftParen:
                Advance();
                var inner = ParseTernary();
                Expect(TokenKind.RightParen);
                return inner;

            case TokenKind.Identifier:
                return ParseIdentifier();

            default:
                throw new FxParseException($"Unexpected token '{token.Kind}' at position {token.Position}.");
        }
    }

    private FxNode ParseIdentifier()
    {
        string name = Current.TextValue;
        int pos = Current.Position;

        // Check for function call: name(args)
        if (Functions.TryGetValue(name, out var funcInfo) && _tokens[_pos + 1].Kind == TokenKind.LeftParen)
        {
            Advance(); // consume name
            Advance(); // consume (
            var args = ParseArgList();
            Expect(TokenKind.RightParen);
            if (args.Count != funcInfo.argCount)
                throw new FxParseException($"Function '{name}' expects {funcInfo.argCount} argument(s) but got {args.Count} at position {pos}.");
            return new FunctionCallNode(funcInfo.func, args.ToArray());
        }

        // Pixel reference: u, v, s
        if (name is "u" or "v" or "s")
        {
            Advance();
            int imageIndex = name == "v" ? 1 : (name == "s" ? 0 : -1);
            FxNode? indexExpr = null;

            // u[expr]
            if (Current.Kind == TokenKind.LeftBracket)
            {
                Advance();
                indexExpr = ParseTernary();
                Expect(TokenKind.RightBracket);
                imageIndex = -1; // will be resolved at eval time
            }

            // Channel accessor handled in ParsePostfix via .channel
            return new PixelAccessNode(imageIndex, ChannelSelector.Default, indexExpr);
        }

        // Variables
        if (Variables.TryGetValue(name, out var varKind))
        {
            Advance();
            return new VariableNode(varKind);
        }

        // Constants
        if (Constants.TryGetValue(name, out double constVal))
        {
            Advance();
            return new NumberNode(constVal);
        }

        throw new FxParseException($"Unknown identifier '{name}' at position {pos}.");
    }

    private List<FxNode> ParseArgList()
    {
        var args = new List<FxNode>();
        if (Current.Kind == TokenKind.RightParen) return args;
        args.Add(ParseTernary());
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            args.Add(ParseTernary());
        }
        return args;
    }
}

// ══════════════════════════════════════════════════════════════
//  Exception type
// ══════════════════════════════════════════════════════════════

public sealed class FxParseException : Exception
{
    public FxParseException(string message) : base(message) { }
}
