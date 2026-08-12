using System.Globalization;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>
/// The copper decoded from one RS-274X Gerber file: the net of copper on that layer as exact
/// <see cref="CurvedRegion2d"/>s, and the coordinate <see cref="Format"/> the file declared.
/// </summary>
/// <param name="Copper">The copper regions on the layer — the dark imaging minus the clear, in file
/// order. This is what the round-trip oracle compares against the source copper model.</param>
/// <param name="Format">The coordinate format read from the file's `%FS%` line.</param>
public sealed record GerberImage(IReadOnlyList<CurvedRegion2d> Copper, GerberFormat Format);

/// <summary>
/// A minimal RS-274X (extended Gerber) reader — the TWIN DECODER of <see cref="GerberBuilder"/>, and
/// the whole point of the round-trip oracle (a structural validator is not enough: the geometry must
/// survive the round trip). It parses exactly what the writer emits — format, unit, standard apertures
/// (C/R/O/P), flashes, linear draws, circular-arc region contours and dark/clear polarity —
/// reconstructs each object as a <see cref="CurvedRegion2d"/>, and images them in file order (dark =
/// union, clear = difference) so the recovered copper equals the model's on the layer.
///
/// <para><b>It is an EXPORT round-trip decoder, not a foreign Gerber importer</b>: its subset is what
/// this writer produces, and anything outside it — an aperture macro, an unknown parameter, a
/// truncated file — is refused BY NAME rather than guessed at (the `StepReader`/`IgesReader` ethos).</para>
/// </summary>
public static class GerberReader
{
    /// <summary>Decodes a Gerber file into the copper it images.</summary>
    /// <exception cref="FormatException">The file is not a well-formed Gerber of the writer's subset —
    /// a missing format/unit spec, a truncated file (no `M02`), an unknown command, an undefined
    /// aperture, or a construct outside the subset — each refused by name.</exception>
    public static GerberImage Read(string gerber)
    {
        ArgumentNullException.ThrowIfNull(gerber);
        return new Parser(gerber).Run();
    }

    private sealed class Parser(string text)
    {
        private enum Interp { Linear, Cw, Ccw }

        private readonly Dictionary<int, GerberAperture> _apertures = [];
        private GerberFormat? _format;
        private bool _unitMm;
        private bool _ended;

        private double _x, _y;
        private int _aperture = -1;
        private Interp _interp = Interp.Linear;
        private bool _dark = true;

        // Batched same-polarity imaging (the writer emits three polarity runs, so this is ~3 booleans).
        private IReadOnlyList<CurvedRegion2d> _image = [];
        private readonly List<CurvedRegion2d> _batch = [];
        private bool _batchDark = true;

        // Stroke run (out-of-region draws) and region contour accumulation.
        private readonly List<Vector2d> _drawRun = [];
        private bool _inRegion;
        private readonly List<CurvedEdge2d> _contour = [];

        internal GerberImage Run()
        {
            int pos = 0;
            while (pos < text.Length)
            {
                char c = text[pos];
                if (char.IsWhiteSpace(c))
                {
                    pos++;
                    continue;
                }
                if (c == '%')
                {
                    int close = text.IndexOf('%', pos + 1);
                    if (close < 0)
                        throw new FormatException("A Gerber parameter block (%…%) is not closed.");
                    string block = text[(pos + 1)..close];
                    foreach (var stmt in block.Split('*', StringSplitOptions.RemoveEmptyEntries))
                        ProcessExtended(stmt.Trim());
                    pos = close + 1;
                }
                else
                {
                    int star = text.IndexOf('*', pos);
                    if (star < 0)
                        throw new FormatException("A Gerber command is not terminated with '*'.");
                    ProcessWord(text[pos..star].Trim());
                    pos = star + 1;
                }
            }

            if (_format is null)
                throw new FormatException("A Gerber file must declare a coordinate format (%FS…%).");
            if (!_unitMm)
                throw new FormatException("A Gerber file must declare millimetre units (%MOMM%).");
            if (!_ended)
                throw new FormatException("A Gerber file ended without its end-of-file command (M02).");

            FlushStroke();
            FlushBatch();
            return new GerberImage(_image, _format.Value);
        }

        // ---- extended (parameter) commands ----

        private void ProcessExtended(string s)
        {
            if (s.StartsWith("FS", StringComparison.Ordinal))
                ParseFormat(s);
            else if (s.StartsWith("MO", StringComparison.Ordinal))
                ParseUnit(s);
            else if (s.StartsWith("ADD", StringComparison.Ordinal))
                ParseAperture(s);
            else if (s == "LPD")
                SetPolarity(dark: true);
            else if (s == "LPC")
                SetPolarity(dark: false);
            else if (s.StartsWith("AM", StringComparison.Ordinal))
                throw new FormatException(
                    "This Gerber reader does not support aperture macros (%AM…%); it decodes only the "
                    + "standard apertures (C/R/O/P) this exporter emits.");
            else if (s.StartsWith("TF", StringComparison.Ordinal) || s.StartsWith("TO", StringComparison.Ordinal)
                  || s.StartsWith("TA", StringComparison.Ordinal) || s.StartsWith("TD", StringComparison.Ordinal))
            {
                // X2 attributes (%TF file / %TA aperture / %TO object / %TD delete) carry METADATA, not
                // geometry — the twin decoder ignores them, so an X2 file round-trips its copper exactly.
            }
            else
                throw new FormatException($"Unsupported Gerber parameter command '%{s}%'.");
        }

        private void ParseFormat(string s)
        {
            // FS L A X{i}{f} Y{i}{f}
            int xi = s.IndexOf('X');
            int yi = s.IndexOf('Y');
            if (xi < 0 || yi < 0 || xi + 2 >= s.Length || yi + 2 >= s.Length)
                throw new FormatException($"Malformed Gerber format specification '%{s}%'.");
            int intDigits = s[xi + 1] - '0';
            int fracDigits = s[xi + 2] - '0';
            if (intDigits is < 1 or > 9 || fracDigits is < 1 or > 9)
                throw new FormatException($"Malformed Gerber coordinate digits in '%{s}%'.");
            _format = new GerberFormat(intDigits, fracDigits);
        }

        private void ParseUnit(string s)
        {
            if (s == "MOMM")
                _unitMm = true;
            else if (s == "MOIN")
                throw new FormatException(
                    "This Gerber reader accepts millimetre units only (%MOMM%); it is the round-trip "
                    + "decoder for an exporter that always writes millimetres.");
            else
                throw new FormatException($"Unrecognised Gerber unit command '%{s}%'.");
        }

        private void ParseAperture(string s)
        {
            // ADD{code}{shape},{params}
            int comma = s.IndexOf(',');
            string head = comma < 0 ? s : s[..comma];
            string tail = comma < 0 ? "" : s[(comma + 1)..];
            // head = "ADD" + digits + shape-letter
            int p = 3;
            int start = p;
            while (p < head.Length && char.IsDigit(head[p]))
                p++;
            if (p == start || p >= head.Length)
                throw new FormatException($"Malformed aperture definition '%{s}%'.");
            int code = int.Parse(head[start..p], CultureInfo.InvariantCulture);
            char shape = head[p];
            var parts = tail.Length == 0 ? [] : tail.Split('X');
            GerberAperture aperture = shape switch
            {
                'C' => GerberAperture.Circle(Num(parts, 0, s)),
                'R' => GerberAperture.Rectangle(Num(parts, 0, s), Num(parts, 1, s)),
                'O' => GerberAperture.Obround(Num(parts, 0, s), Num(parts, 1, s)),
                'P' => GerberAperture.Polygon(
                    Num(parts, 0, s), (int)Num(parts, 1, s), parts.Length > 2 ? Num(parts, 2, s) : 0),
                _ => throw new FormatException(
                    $"Unsupported aperture shape '{shape}' in '%{s}%' (only C/R/O/P standard apertures)."),
            };
            _apertures[code] = aperture;
        }

        private static double Num(string[] parts, int index, string ctx)
        {
            if (index >= parts.Length || !double.TryParse(
                    parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new FormatException($"Missing or malformed aperture parameter in '%{ctx}%'.");
            return v;
        }

        // ---- function commands ----

        private void ProcessWord(string w)
        {
            if (w.Length == 0)
                return;

            int idx = 0;
            if (w[0] == 'G')
            {
                idx = 1;
                int start = idx;
                while (idx < w.Length && char.IsDigit(w[idx]))
                    idx++;
                int g = int.Parse(w[start..idx], CultureInfo.InvariantCulture);
                ApplyG(g);
                if (g == 4)
                    return;   // a comment — ignore any trailing text
            }

            string rest = w[idx..];
            if (rest.Length == 0)
                return;

            if (rest[0] == 'M')
            {
                int m = int.Parse(rest[1..], CultureInfo.InvariantCulture);
                if (m is 2 or 0 or 30)
                    _ended = true;
                return;
            }
            if (rest[0] == 'D' && rest.Length > 1 && (char.IsDigit(rest[1]) || rest[1] is '+' or '-'))
            {
                int code = int.Parse(rest[1..], CultureInfo.InvariantCulture);
                if (code >= 10)
                {
                    SelectAperture(code);
                    return;
                }
                Operation(code, null, null);   // a bare D01/D02/D03 at the current point
                return;
            }
            if (rest[0] is 'X' or 'Y' or 'I' or 'J')
            {
                ParseCoordinateWord(rest);
                return;
            }
            throw new FormatException($"Unrecognised Gerber command '{w}'.");
        }

        private void ApplyG(int g)
        {
            switch (g)
            {
                case 1: _interp = Interp.Linear; break;
                case 2: _interp = Interp.Cw; break;
                case 3: _interp = Interp.Ccw; break;
                case 4: break;                      // comment
                case 36: BeginRegion(); break;
                case 37: EndRegion(); break;
                case 74: break;                      // single-quadrant (we emit multi-quadrant arcs)
                case 75: break;                      // multi-quadrant
                default:
                    throw new FormatException($"Unsupported Gerber G-code 'G{g:00}'.");
            }
        }

        private void SelectAperture(int code)
        {
            if (!_apertures.ContainsKey(code))
                throw new FormatException($"Gerber selects aperture D{code}, which was never defined.");
            FlushStroke();
            _aperture = code;
        }

        private void ParseCoordinateWord(string rest)
        {
            if (_format is null)
                throw new FormatException(
                    "A Gerber coordinate appeared before the coordinate format was declared (%FS…%).");
            double? x = null, y = null, i = null, j = null;
            int? op = null;
            int p = 0;
            while (p < rest.Length)
            {
                char c = rest[p++];
                int start = p;
                if (p < rest.Length && (rest[p] == '+' || rest[p] == '-'))
                    p++;
                while (p < rest.Length && char.IsDigit(rest[p]))
                    p++;
                string num = rest[start..p];
                if (num.Length == 0 || num == "+" || num == "-")
                    throw new FormatException($"Malformed Gerber coordinate token '{c}' in '{rest}'.");
                long raw = long.Parse(num, CultureInfo.InvariantCulture);
                switch (c)
                {
                    case 'X': x = _format!.Value.Decode(raw); break;
                    case 'Y': y = _format!.Value.Decode(raw); break;
                    case 'I': i = _format!.Value.Decode(raw); break;
                    case 'J': j = _format!.Value.Decode(raw); break;
                    case 'D': op = (int)raw; break;
                    default:
                        throw new FormatException($"Unrecognised Gerber coordinate token '{c}' in '{rest}'.");
                }
            }
            Operation(op, i, j, x, y);
        }

        private void Operation(int? op, double? i, double? j, double? x = null, double? y = null)
        {
            var prev = new Vector2d(_x, _y);
            if (x.HasValue) _x = x.Value;
            if (y.HasValue) _y = y.Value;
            var cur = new Vector2d(_x, _y);
            if (op is null)
                return;

            switch (op.Value)
            {
                case 1:   // draw / interpolate
                    if (_inRegion)
                        AddContourEdge(prev, cur, i, j);
                    else
                        _drawRun.Add(cur);
                    break;
                case 2:   // move (pen up)
                    if (_inRegion)
                    {
                        FlushContour();      // a new D02 begins a fresh contour
                    }
                    else
                    {
                        FlushStroke();
                        _drawRun.Add(cur);   // the run starts at the move point
                    }
                    break;
                case 3:   // flash
                    if (_inRegion)
                        throw new FormatException("A flash (D03) is not allowed inside a region (G36/G37).");
                    FlushStroke();
                    FlashAperture(cur);
                    break;
                default:
                    throw new FormatException($"Unsupported Gerber operation code D{op:00}.");
            }
        }

        // ---- regions ----

        private void BeginRegion()
        {
            FlushStroke();
            _inRegion = true;
            _contour.Clear();
        }

        private void EndRegion()
        {
            FlushContour();
            _inRegion = false;
        }

        private void AddContourEdge(in Vector2d from, in Vector2d to, double? i, double? j)
        {
            if (from.DistanceTo(to) <= CurvedRegion2d.BoundaryTolerance && _interp == Interp.Linear)
                return;   // a zero-length linear step
            if (_interp == Interp.Linear)
            {
                _contour.Add(CurvedEdge2d.Line(from, to));
                return;
            }
            var center = new Vector2d(from.X + (i ?? 0), from.Y + (j ?? 0));
            double radius = center.DistanceTo(from);
            if (!(radius > 0))
                throw new FormatException("A Gerber arc has a zero radius (I/J offset is zero).");
            _contour.Add(CurvedEdge2d.ArcBetween(from, to, center, radius, _interp == Interp.Ccw ? 1 : -1));
        }

        private void FlushContour()
        {
            if (_contour.Count == 0)
                return;
            var loop = new List<CurvedEdge2d>(_contour);
            _contour.Clear();
            // Close the loop if the last edge does not return exactly to the start (weld tier).
            var start = loop[0].Start;
            var end = loop[^1].End;
            if (end.DistanceTo(start) > CurvedRegion2d.BoundaryTolerance)
                loop.Add(CurvedEdge2d.Line(end, start));
            AddToBatch(new CurvedRegion2d(loop));
        }

        // ---- flashes and strokes ----

        private void FlashAperture(in Vector2d at)
        {
            if (_aperture < 0)
                throw new FormatException("A flash (D03) was issued before any aperture was selected.");
            var a = _apertures[_aperture];
            var region = a.Shape switch
            {
                GerberApertureShape.Circle => GerberShapes.Circle(at, a.A),
                GerberApertureShape.Rectangle => GerberShapes.Rectangle(at, a.A, a.B),
                GerberApertureShape.Obround => GerberShapes.Obround(at, a.A, a.B),
                GerberApertureShape.Polygon => GerberShapes.Polygon(at, a.A, a.Vertices, a.Rotation),
                _ => throw new FormatException($"Cannot flash aperture shape {a.Shape}."),
            };
            AddToBatch(region);
        }

        private void FlushStroke()
        {
            if (_drawRun.Count < 2)
            {
                _drawRun.Clear();
                return;
            }
            if (_aperture < 0)
                throw new FormatException("A draw (D01) was issued before any aperture was selected.");
            var a = _apertures[_aperture];
            if (a.Shape != GerberApertureShape.Circle)
                throw new FormatException(
                    $"A draw (D01) uses a {a.Shape} aperture; this reader strokes draws with round "
                    + "apertures only (the shape this exporter emits for traces).");

            var edges = new List<CurvedEdge2d>(_drawRun.Count - 1);
            for (int k = 1; k < _drawRun.Count; k++)
                if (_drawRun[k - 1].DistanceTo(_drawRun[k]) > CurvedRegion2d.BoundaryTolerance)
                    edges.Add(CurvedEdge2d.Line(_drawRun[k - 1], _drawRun[k]));
            _drawRun.Clear();
            if (edges.Count == 0)
                return;

            foreach (var region in CurvedRegion2dOffset.Stroke(edges, a.A, StrokeCap.Round, OffsetJoin.Round))
                AddToBatch(region);
        }

        // ---- polarity-batched imaging ----

        private void SetPolarity(bool dark)
        {
            if (dark == _dark)
                return;
            FlushStroke();     // a stroke belongs to the polarity in force while it was drawn
            FlushBatch();
            _dark = dark;
        }

        private void AddToBatch(CurvedRegion2d region)
        {
            if (_batch.Count == 0)
                _batchDark = _dark;
            _batch.Add(region);
        }

        private void FlushBatch()
        {
            if (_batch.Count == 0)
                return;
            var merged = CurvedRegion2dBoolean.UnionAll(_batch);
            _image = _batchDark
                ? CurvedRegion2dBoolean.Union(_image, merged)
                : CurvedRegion2dBoolean.Difference(_image, merged);
            _batch.Clear();
        }
    }
}
