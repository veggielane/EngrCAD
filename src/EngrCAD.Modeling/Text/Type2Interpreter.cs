using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// The charstring machine both PostScript outline formats run on: the operand stack,
/// the number encodings, subroutine calls, and every path operator from <c>rmoveto</c>
/// to the flex family. <see cref="CffOutlines"/> (Type 2, <c>CFF </c>) and
/// <see cref="Cff2Outlines"/> (CFF2) differ in a handful of operators and in nothing
/// else, so the curve arithmetic lives here ONCE — the alternative is two copies of
/// <c>hvcurveto</c>'s five-argument last group, which is exactly the kind of rule that
/// gets fixed in one copy and not the other.
/// <para>Subclasses supply what genuinely differs: Type 2's leading width operand and
/// <c>endchar</c> (which CFF2 does not have at all), and CFF2's <c>blend</c> and
/// <c>vsindex</c> (which Type 2 does not).</para>
/// </summary>
internal abstract class Type2Interpreter
{
    /// <summary>Subroutine nesting limit (Type 2 Appendix B).</summary>
    protected const int MaxSubrDepth = 10;

    private readonly byte[] _data;
    private readonly (int Start, int End)[] _localSubrs;
    private readonly (int Start, int End)[] _globalSubrs;
    private double _x, _y;
    private int _stems;
    private List<GlyphPoint>? _contour;

    protected Type2Interpreter(
        byte[] data, (int Start, int End)[] localSubrs, (int Start, int End)[] globalSubrs, int glyphIndex)
    {
        _data = data;
        _localSubrs = localSubrs;
        _globalSubrs = globalSubrs;
        GlyphIndex = glyphIndex;
    }

    /// <summary>The glyph being decoded — every refusal names it.</summary>
    protected int GlyphIndex { get; }

    /// <summary>The operand stack, in push order.</summary>
    protected List<double> Stack { get; } = [];

    /// <summary>Set by <c>endchar</c> (Type 2 only); stops the run.</summary>
    protected bool Ended { get; set; }

    /// <summary>Type 2's argument stack limit; CFF2 raises it for <c>blend</c>.</summary>
    protected virtual int StackLimit => 48;

    /// <summary>Closed contours decoded so far.</summary>
    public List<GlyphContour> Contours { get; } = [];

    /// <summary>Bias added to subroutine call operands (Type 2 §4.7): small fonts get
    /// small operands for the common subrs.</summary>
    protected static int Bias(int subrCount) => subrCount < 1240 ? 107 : subrCount < 33900 ? 1131 : 32768;

    public void Run(int start, int end, int depth)
    {
        if (depth > MaxSubrDepth)
            throw new FontFormatException(
                $"Glyph {GlyphIndex}: charstring subroutines nest deeper than {MaxSubrDepth} (cyclic call?).");

        var reader = new FontReader(_data.AsSpan(), start);
        while (reader.Position < end && !Ended)
        {
            int b0 = reader.ReadUInt8();
            if (b0 >= 32 || b0 == 28)
            {
                Push(ReadNumber(ref reader, b0));
                continue;
            }
            switch (b0)
            {
                case 1 or 3 or 18 or 23:                 // hstem / vstem / hstemhm / vstemhm
                    CountStems();
                    break;
                case 19 or 20:                           // hintmask / cntrmask
                    CountStems();                        // arguments still on the stack are an implicit vstem list
                    reader.Skip((_stems + 7) / 8);       // one data byte per eight stems — THE counting subtlety
                    break;
                case 21:                                 // rmoveto
                    BeforeMove(expected: 2);
                    MoveTo(_x + Arg(0), _y + Arg(1));
                    break;
                case 22:                                 // hmoveto
                    BeforeMove(expected: 1);
                    MoveTo(_x + Arg(0), _y);
                    break;
                case 4:                                  // vmoveto
                    BeforeMove(expected: 1);
                    MoveTo(_x, _y + Arg(0));
                    break;
                case 5:                                  // rlineto
                    for (int i = 0; i + 1 < Stack.Count; i += 2)
                        LineTo(_x + Arg(i), _y + Arg(i + 1));
                    Stack.Clear();
                    break;
                case 6 or 7:                             // hlineto / vlineto (alternating)
                    AlternatingLines(startHorizontal: b0 == 6);
                    break;
                case 8:                                  // rrcurveto
                    for (int i = 0; i + 5 < Stack.Count; i += 6)
                        RelativeCurve(i);
                    Stack.Clear();
                    break;
                case 24:                                 // rcurveline: curves then one line
                {
                    int i = 0;
                    for (; i + 7 < Stack.Count; i += 6)
                        RelativeCurve(i);
                    LineTo(_x + Arg(i), _y + Arg(i + 1));
                    Stack.Clear();
                    break;
                }
                case 25:                                 // rlinecurve: lines then one curve
                {
                    int i = 0;
                    for (; Stack.Count - i > 6; i += 2)
                        LineTo(_x + Arg(i), _y + Arg(i + 1));
                    RelativeCurve(i);
                    Stack.Clear();
                    break;
                }
                case 26 or 27:                           // vvcurveto / hhcurveto
                    AxisAlignedCurves(vertical: b0 == 26);
                    break;
                case 30 or 31:                           // vhcurveto / hvcurveto (alternating)
                    AlternatingCurves(startHorizontal: b0 == 31);
                    break;
                case 10:                                 // callsubr
                    Call(_localSubrs, "local", depth);
                    break;
                case 29:                                 // callgsubr
                    Call(_globalSubrs, "global", depth);
                    break;
                case 11:                                 // return
                    return;
                case 12:
                    Escaped(reader.ReadUInt8());
                    break;
                default:
                    if (!Extend(b0, ref reader))
                        throw new FontFormatException(
                            $"Glyph {GlyphIndex}: charstring operator {b0} is not valid {Dialect}.");
                    break;
            }
        }
    }

    /// <summary>Names this dialect in refusals ("Type 2" / "CFF2").</summary>
    protected abstract string Dialect { get; }

    /// <summary>An operator the shared machine does not know: Type 2's <c>endchar</c>,
    /// CFF2's <c>vsindex</c> and <c>blend</c>. Returns false to refuse by name.</summary>
    protected virtual bool Extend(int op, ref FontReader reader) => false;

    /// <summary>Runs before a moveto consumes its arguments — Type 2 strips the leading
    /// width operand here; CFF2 has no width in its charstrings at all.</summary>
    protected virtual void BeforeMove(int expected) { }

    /// <summary>Runs before a stem-hint operator counts its pairs, for the same
    /// reason.</summary>
    protected virtual void BeforeStems() { }

    public void FlushContour()
    {
        if (_contour is { Count: >= 2 })
            Contours.Add(new GlyphContour([.. _contour], isCubic: true));
        _contour = null;
    }

    // ---- numbers and the stack ----------------------------------------------

    protected static double ReadNumber(ref FontReader reader, int b0) => b0 switch
    {
        28 => (short)reader.ReadUInt16(),
        255 => (int)reader.ReadUInt32() / 65536.0,       // 16.16 fixed point (charstrings only)
        >= 32 and <= 246 => b0 - 139,
        >= 247 and <= 250 => (b0 - 247) * 256 + reader.ReadUInt8() + 108,
        >= 251 and <= 254 => -(b0 - 251) * 256 - reader.ReadUInt8() - 108,
        _ => throw new FontFormatException($"Charstring byte {b0} is not a number."),
    };

    protected void Push(double value)
    {
        if (Stack.Count >= StackLimit)
            throw new FontFormatException(
                $"Glyph {GlyphIndex}: charstring exceeds the argument stack limit of {StackLimit}.");
        Stack.Add(value);
    }

    protected double Arg(int index) =>
        index < Stack.Count
            ? Stack[index]
            : throw new FontFormatException(
                $"Glyph {GlyphIndex}: charstring operator is missing arguments ({Stack.Count} on the stack).");

    private void CountStems()
    {
        BeforeStems();
        _stems += Stack.Count / 2;
        Stack.Clear();
    }

    // ---- path construction ---------------------------------------------------

    private void MoveTo(double x, double y)
    {
        FlushContour();
        _contour = [new GlyphPoint(new Vector2d(x, y), OnCurve: true)];
        _x = x;
        _y = y;
        Stack.Clear();
    }

    private void LineTo(double x, double y)
    {
        RequireContour().Add(new GlyphPoint(new Vector2d(x, y), OnCurve: true));
        _x = x;
        _y = y;
    }

    private void CurveTo(double c1X, double c1Y, double c2X, double c2Y, double x, double y)
    {
        var contour = RequireContour();
        contour.Add(new GlyphPoint(new Vector2d(c1X, c1Y), OnCurve: false));
        contour.Add(new GlyphPoint(new Vector2d(c2X, c2Y), OnCurve: false));
        contour.Add(new GlyphPoint(new Vector2d(x, y), OnCurve: true));
        _x = x;
        _y = y;
    }

    private List<GlyphPoint> RequireContour() =>
        _contour ?? throw new FontFormatException($"Glyph {GlyphIndex}: charstring draws before any moveto.");

    /// <summary>One rrcurveto sextet at stack offset <paramref name="i"/>.</summary>
    private void RelativeCurve(int i)
    {
        double c1X = _x + Arg(i), c1Y = _y + Arg(i + 1);
        double c2X = c1X + Arg(i + 2), c2Y = c1Y + Arg(i + 3);
        CurveTo(c1X, c1Y, c2X, c2Y, c2X + Arg(i + 4), c2Y + Arg(i + 5));
    }

    private void AlternatingLines(bool startHorizontal)
    {
        bool horizontal = startHorizontal;
        for (int i = 0; i < Stack.Count; i++)
        {
            if (horizontal)
                LineTo(_x + Arg(i), _y);
            else
                LineTo(_x, _y + Arg(i));
            horizontal = !horizontal;
        }
        Stack.Clear();
    }

    /// <summary>hhcurveto / vvcurveto: runs of curves whose ends stay on one axis;
    /// an odd leading argument is the first curve's cross-axis start delta.</summary>
    private void AxisAlignedCurves(bool vertical)
    {
        int i = 0;
        double lead = 0;
        if (Stack.Count % 4 == 1)
        {
            lead = Arg(0);
            i = 1;
        }
        for (; i + 3 < Stack.Count; i += 4)
        {
            double c1X, c1Y;
            if (vertical)
            {
                c1X = _x + lead;
                c1Y = _y + Arg(i);
            }
            else
            {
                c1X = _x + Arg(i);
                c1Y = _y + lead;
            }
            double c2X = c1X + Arg(i + 1), c2Y = c1Y + Arg(i + 2);
            if (vertical)
                CurveTo(c1X, c1Y, c2X, c2Y, c2X, c2Y + Arg(i + 3));
            else
                CurveTo(c1X, c1Y, c2X, c2Y, c2X + Arg(i + 3), c2Y);
            lead = 0;
        }
        Stack.Clear();
    }

    /// <summary>hvcurveto / vhcurveto: groups of four alternating which axis the
    /// curve starts and ends on; the final group may carry a fifth argument for
    /// the otherwise-implied cross component of the last end point.</summary>
    private void AlternatingCurves(bool startHorizontal)
    {
        bool horizontal = startHorizontal;
        int i = 0;
        while (Stack.Count - i >= 4)
        {
            bool last = Stack.Count - i == 5;
            double c1X, c1Y;
            if (horizontal)
            {
                c1X = _x + Arg(i);
                c1Y = _y;
            }
            else
            {
                c1X = _x;
                c1Y = _y + Arg(i);
            }
            double c2X = c1X + Arg(i + 1), c2Y = c1Y + Arg(i + 2);
            double endX, endY;
            if (horizontal)
            {
                endY = c2Y + Arg(i + 3);
                endX = c2X + (last ? Arg(i + 4) : 0);
            }
            else
            {
                endX = c2X + Arg(i + 3);
                endY = c2Y + (last ? Arg(i + 4) : 0);
            }
            CurveTo(c1X, c1Y, c2X, c2Y, endX, endY);
            horizontal = !horizontal;
            i += 4;
        }
        Stack.Clear();
    }

    private void Call((int Start, int End)[] subrs, string kind, int depth)
    {
        if (Stack.Count == 0)
            throw new FontFormatException(
                $"Glyph {GlyphIndex}: call{(kind == "global" ? "gsubr" : "subr")} with an empty stack.");
        int index = (int)Stack[^1] + Bias(subrs.Length);
        Stack.RemoveAt(Stack.Count - 1);
        if ((uint)index >= (uint)subrs.Length)
            throw new FontFormatException(
                $"Glyph {GlyphIndex}: {kind} subroutine {index} does not exist ({subrs.Length} defined).");
        Run(subrs[index].Start, subrs[index].End, depth + 1);
    }

    private void Escaped(int op)
    {
        switch (op)
        {
            case 35:                                     // flex: two curves, then the fd hint depth
                RelativeCurve(0);
                RelativeCurve(6);
                Stack.Clear();
                break;
            case 34:                                     // hflex: both curves level with the start
            {
                double y0 = _y;
                double c1X = _x + Arg(0), c1Y = _y;
                double c2X = c1X + Arg(1), c2Y = c1Y + Arg(2);
                CurveTo(c1X, c1Y, c2X, c2Y, c2X + Arg(3), c2Y);
                double c3X = _x + Arg(4), c3Y = _y;
                double c4X = c3X + Arg(5), c4Y = y0;
                CurveTo(c3X, c3Y, c4X, c4Y, c4X + Arg(6), y0);
                Stack.Clear();
                break;
            }
            case 36:                                     // hflex1: ends level with the start
            {
                double y0 = _y;
                double c1X = _x + Arg(0), c1Y = _y + Arg(1);
                double c2X = c1X + Arg(2), c2Y = c1Y + Arg(3);
                CurveTo(c1X, c1Y, c2X, c2Y, c2X + Arg(4), c2Y);
                double c3X = _x + Arg(5), c3Y = _y;
                double c4X = c3X + Arg(6), c4Y = c3Y + Arg(7);
                CurveTo(c3X, c3Y, c4X, c4Y, c4X + Arg(8), y0);
                Stack.Clear();
                break;
            }
            case 37:                                     // flex1: the end's implied component closes the deltas
            {
                double startX = _x, startY = _y;
                double dX = Arg(0) + Arg(2) + Arg(4) + Arg(6) + Arg(8);
                double dY = Arg(1) + Arg(3) + Arg(5) + Arg(7) + Arg(9);
                RelativeCurve(0);
                double c1X = _x + Arg(6), c1Y = _y + Arg(7);
                double c2X = c1X + Arg(8), c2Y = c1Y + Arg(9);
                if (Math.Abs(dX) > Math.Abs(dY))
                    CurveTo(c1X, c1Y, c2X, c2Y, c2X + Arg(10), startY);
                else
                    CurveTo(c1X, c1Y, c2X, c2Y, startX, c2Y + Arg(10));
                Stack.Clear();
                break;
            }
            default:
                throw new FontFormatException(
                    $"Glyph {GlyphIndex}: charstring operator 12 {op} (arithmetic/storage extension) is not supported.");
        }
    }
}
